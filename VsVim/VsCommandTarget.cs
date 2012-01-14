﻿using System;
using EditorUtils;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Vim;
using Vim.Extensions;
using Vim.UI.Wpf;

namespace VsVim
{
    /// <summary>
    /// This class needs to intercept commands which the core VIM engine wants to process and call into the VIM engine 
    /// directly.  It needs to be very careful to not double use commands that will be processed by the KeyProcessor.  In 
    /// general it just needs to avoid processing text input
    /// </summary>
    internal sealed class VsCommandTarget : IOleCommandTarget
    {
        private enum CommandStatus
        {
            /// <summary>
            /// Command is enabled
            /// </summary>
            Enable,

            /// <summary>
            /// Command is disabled
            /// </summary>
            Disable,

            /// <summary>
            /// VsVim isn't concerned about the command and it's left to the next IOleCommandTarget
            /// to determine if it's enabled or not
            /// </summary>
            PassOn,
        }

        /// <summary>
        /// This is the key which is used to store VsCommandTarget instances in the ITextView
        /// property bag
        /// </summary>
        private static readonly object Key = new object();

        private readonly IVimBuffer _buffer;
        private readonly IVimBufferCoordinator _bufferCoordinator;
        private readonly ITextBuffer _textBuffer;
        private readonly IVsAdapter _vsAdapter;
        private readonly IDisplayWindowBroker _broker;
        private readonly IExternalEditorManager _externalEditManager;
        private IOleCommandTarget _nextTarget;

        private VsCommandTarget(
            IVimBufferCoordinator bufferCoordinator,
            IVsAdapter vsAdapter,
            IDisplayWindowBroker broker,
            IExternalEditorManager externalEditorManager)
        {
            _buffer = bufferCoordinator.VimBuffer;
            _bufferCoordinator = bufferCoordinator;
            _textBuffer = _buffer.TextBuffer;
            _vsAdapter = vsAdapter;
            _broker = broker;
            _externalEditManager = externalEditorManager;
        }

        /// <summary>
        /// Try and custom process the given InsertCommand when it's appropriate to override
        /// with Visual Studio specific behavior
        /// </summary>
        public bool TryCustomProcess(InsertCommand command)
        {
            var oleCommandData = OleCommandData.Empty;
            try
            {
                if (!TryGetOleCommandData(command, out oleCommandData))
                {
                    // Not a command that we custom process
                    return false;
                }
                var versionNumber = _textBuffer.CurrentSnapshot.Version.VersionNumber;
                int hr = _nextTarget.Exec(oleCommandData);

                // Whether or not an Exec succeeded is a bit of a heuristic.  IOleCommandTarget implementations like
                // C++ will return E_ABORT if Intellisense failed but the character was actually inserted into 
                // the ITextBuffer.  VsVim really only cares about the character insert.  However we must also
                // consider cases where the character successfully resulted in no action as a success
                return ErrorHandler.Succeeded(hr) || versionNumber < _textBuffer.CurrentSnapshot.Version.VersionNumber;
            }
            finally
            {
                if (oleCommandData != null)
                {
                    oleCommandData.Dispose();
                }
            }
        }

        /// <summary>
        /// Try and convert the given insert command to an OleCommand.  This should only be done
        /// for InsertCommand values which we want to custom process
        /// </summary>
        private bool TryGetOleCommandData(InsertCommand command, out OleCommandData commandData)
        {
            if (command.IsBack)
            {
                commandData = new OleCommandData(VSConstants.VSStd2KCmdID.BACKSPACE);
                return true;
            }

            if (command.IsDelete)
            {
                commandData = new OleCommandData(VSConstants.VSStd2KCmdID.DELETE);
                return true;
            }

            if (command.IsDirectInsert)
            {
                var directInsert = (InsertCommand.DirectInsert)command;
                commandData = OleCommandData.CreateTypeChar(directInsert.Item);
                return true;
            }

            if (command.IsInsertTab)
            {
                commandData = new OleCommandData(VSConstants.VSStd2KCmdID.TAB);
                return true;
            }

            if (command.IsInsertNewLine)
            {
                commandData = new OleCommandData(VSConstants.VSStd2KCmdID.RETURN);
                return true;
            }

            commandData = OleCommandData.Empty;
            return false;
        }

        /// <summary>
        /// Try and map a KeyInput to a single KeyInput value.  This will only succeed for KeyInput 
        /// values which have no mapping or map to a single KeyInput value
        /// </summary>
        private bool TryGetSingleMapping(KeyInput original, out KeyInput mapped)
        {
            var result = _buffer.GetKeyInputMapping(original);
            if (result.IsNeedsMoreInput || result.IsRecursive)
            {
                // No single mapping
                mapped = null;
                return false;
            }

            if (result.IsMapped)
            {
                var set = ((KeyMappingResult.Mapped)result).Item;
                if (!set.IsOneKeyInput)
                {
                    mapped = null;
                    return false;
                }

                mapped = set.FirstKeyInput.Value;
                return true;
            }

            if (result.IsNoMapping)
            {
                // If there is no mapping we still need to consider the case of buffered 
                // KeyInput values.  If there are any buffered KeyInput values then we 
                // have > 1 input values: the current and whatever is mapped
                if (!_buffer.BufferedKeyInputs.IsEmpty)
                {
                    mapped = null;
                    return false;
                }

                // No mapping and no buffered input so it's just a simple normal KeyInput
                // value to be processed
                mapped = original;
                return true;
            }

            // Shouldn't get here because all cases of KeyMappingResult should be
            // handled above
            Contract.Assert(false);
            mapped = null;
            return false;
        }

        /// <summary>
        /// Determine if the IInsertMode value should process the given KeyInput
        /// </summary>
        private bool ShouldProcessWithCommandTargetOverInsertMode(IInsertMode mode, KeyInput keyInput)
        {
            // In the middle of a word completion session let insert mode handle the input.  It's 
            // displaying the intellisense itself and this method is meant to let custom intellisense
            // operate normally
            if (mode.ActiveWordCompletionSession.IsSome())
            {
                return false;
            }

            // Is this a key known to impact IntelliSense
            var isIntelliSenseKey =
                keyInput.Key == VimKey.Up ||
                keyInput.Key == VimKey.Down ||
                keyInput.Key == VimKey.Left ||
                keyInput.Key == VimKey.Right ||
                keyInput.Key == VimKey.Tab ||
                keyInput.Key == VimKey.Back ||
                keyInput == KeyInputUtil.EnterKey;

            // If this is any of the arrow keys and one of the help windows is active then don't 
            // let insert mode process the input.  We want the KeyInput to be routed to the windows
            // like Intellisense so navigation can occur
            if (isIntelliSenseKey && (_broker.IsCompletionActive || _broker.IsQuickInfoActive || _broker.IsSignatureHelpActive || _broker.IsSmartTagSessionActive))
            {
                return true;
            }

            // Unfortunately there is no way to detect if the R# completion windows are active.  We have
            // to take the pessimistic view that they are and just not handle the input
            if (isIntelliSenseKey && _externalEditManager.IsResharperInstalled)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Try and process the KeyInput from the Exec method
        /// </summary>
        private bool TryProcessWithBuffer(KeyInput keyInput)
        {
            if (!_buffer.CanProcess(keyInput))
            {
                // If the IVimBuffer can't process it then it doesn't matter
                return false;
            }

            // Next we need to determine if we can process this directly or not.  The only mode 
            // we actively intercept KeyInput for is InsertMode because we need to route it
            // through IOleCommandTarget to get Intellisense and many other features.
            var insertMode = _buffer.ModeKind == ModeKind.Insert
                ? _buffer.InsertMode
                : null;
            if (insertMode == null)
            {
                return _buffer.Process(keyInput).IsAnyHandled;
            }

            // Next we need to consider here are Key mappings.  The CanProcess and Process APIs 
            // will automatically map the KeyInput under the hood at the IVimBuffer level but
            // not at the individual IMode.  Have to manually map here and test against the 
            // mapped KeyInput
            KeyInput mapped;
            if (TryGetSingleMapping(keyInput, out mapped) && ShouldProcessWithCommandTargetOverInsertMode(insertMode, mapped))
            {
                // We are now intentionally by passing insert mode here.  If there is an active 
                // IWordCompletionSession here we need to manually dismiss it.  Else it will remain
                // as we start typing a new word
                if (insertMode.ActiveWordCompletionSession.IsSome())
                {
                    insertMode.ActiveWordCompletionSession.Value.Dismiss();
                }

                return false;
            }

            return _buffer.Process(keyInput).IsAnyHandled;
        }

        /// <summary>
        /// Try and convert the Visual Studio command to it's equivalent KeyInput
        /// </summary>
        internal bool TryConvert(Guid commandGroup, uint commandId, IntPtr variantIn, out KeyInput keyInput)
        {
            keyInput = null;

            EditCommand editCommand;
            if (!TryConvert(commandGroup, commandId, variantIn, out editCommand))
            {
                return false;
            }

            if (!editCommand.HasKeyInput)
            {
                return false;
            }

            keyInput = editCommand.KeyInput;
            return true;
        }

        /// <summary>
        /// Try and convert the Visual Studio command to it's equivalent KeyInput
        /// </summary>
        internal bool TryConvert(Guid commandGroup, uint commandId, IntPtr variantIn, out EditCommand editCommand)
        {
            editCommand = null;

            // Don't ever process a command when we are in an automation function.  Doing so will cause VsVim to 
            // intercept items like running Macros and certain wizard functionality
            if (_vsAdapter.InAutomationFunction)
            {
                return false;
            }

            // Don't intercept commands while incremental search is active.  Don't want to interfere with it
            if (_vsAdapter.IsIncrementalSearchActive(_buffer.TextView))
            {
                return false;
            }

            var modifiers = KeyUtil.ConvertToKeyModifiers(_vsAdapter.KeyboardDevice.Modifiers);
            if (!OleCommandUtil.TryConvert(commandGroup, commandId, variantIn, modifiers, out editCommand))
            {
                return false;
            }

            // Don't process Visual Studio commands.  If the key sequence is mapped to a Visual Studio command
            // then that command wins.
            if (editCommand.EditCommandKind == EditCommandKind.VisualStudioCommand)
            {
                return false;
            }

            return true;
        }

        int IOleCommandTarget.Exec(ref Guid commandGroup, uint commandId, uint commandExecOpt, IntPtr variantIn, IntPtr variantOut)
        {
            try
            {
                EditCommand editCommand;
                if (TryConvert(commandGroup, commandId, variantIn, out editCommand))
                {
                    if (editCommand.IsUndo)
                    {
                        // The user hit the undo button.  Don't attempt to map anything here and instead just 
                        // run a single Vim undo operation
                        _buffer.UndoRedoOperations.Undo(1);
                        return NativeMethods.S_OK;
                    }
                    else if (editCommand.IsRedo)
                    {
                        // The user hit the redo button.  Don't attempt to map anything here and instead just 
                        // run a single Vim redo operation
                        _buffer.UndoRedoOperations.Redo(1);
                        return NativeMethods.S_OK;
                    }
                    else if (editCommand.HasKeyInput)
                    {
                        var keyInput = editCommand.KeyInput;

                        // Discard the input if it's been flagged by a previous QueryStatus
                        if (_bufferCoordinator.DiscardedKeyInput.IsSome(keyInput))
                        {
                            return NativeMethods.S_OK;
                        }

                        // Try and process the command with the IVimBuffer
                        if (TryProcessWithBuffer(keyInput))
                        {
                            return NativeMethods.S_OK;
                        }
                    }
                }
            }
            finally
            {
                _bufferCoordinator.DiscardedKeyInput = FSharpOption<KeyInput>.None;
            }

            return _nextTarget.Exec(commandGroup, commandId, commandExecOpt, variantIn, variantOut);
        }

        int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            EditCommand editCommand;
            if (1 == cCmds && TryConvert(pguidCmdGroup, prgCmds[0].cmdID, pCmdText, out editCommand))
            {
                _bufferCoordinator.DiscardedKeyInput = FSharpOption<KeyInput>.None;

                var action = CommandStatus.PassOn;
                if (editCommand.IsUndo || editCommand.IsRedo)
                {
                    action = CommandStatus.Enable;
                }
                else if (editCommand.HasKeyInput && _buffer.CanProcess(editCommand.KeyInput))
                {
                    action = CommandStatus.Enable;
                    if (_externalEditManager.IsResharperInstalled)
                    {
                        action = QueryStatusInResharper(editCommand.KeyInput) ?? CommandStatus.Enable;
                    }
                }

                switch (action)
                {
                    case CommandStatus.Enable:
                        prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                        return NativeMethods.S_OK;
                    case CommandStatus.Disable:
                        prgCmds[0].cmdf = (uint)OLECMDF.OLECMDF_SUPPORTED;
                        return NativeMethods.S_OK;
                    case CommandStatus.PassOn:
                        return _nextTarget.QueryStatus(pguidCmdGroup, cCmds, prgCmds, pCmdText);
                }
            }

            return _nextTarget.QueryStatus(pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        /// <summary>
        /// With Resharper installed we need to special certain keys like Escape.  They need to 
        /// process it in order for them to dismiss their custom intellisense but their processing 
        /// will swallow the event and not propagate it to us.  So handle, return and account 
        /// for the double stroke in exec
        /// </summary>
        private CommandStatus? QueryStatusInResharper(KeyInput keyInput)
        {
            CommandStatus? status = null;
            var passToResharper = true;
            if (_buffer.ModeKind.IsAnyInsert() && keyInput == KeyInputUtil.EscapeKey)
            {
                // Have to special case Escape here for insert mode.  R# is typically ahead of us on the IOleCommandTarget
                // chain.  If a completion window is open and we wait for Exec to run R# will be ahead of us and run
                // their Exec call.  This will lead to them closing the completion window and not calling back into
                // our exec leaving us in insert mode.
                status = CommandStatus.Enable;
            }
            else if (_buffer.ModeKind == ModeKind.ExternalEdit && keyInput == KeyInputUtil.EscapeKey)
            {
                // Have to special case Escape here for external edit mode because we want escape to get us back to 
                // normal mode.  However we do want this key to make it to R# as well since they may need to dismiss
                // intellisense
                status = CommandStatus.Enable;
            }
            else if ((keyInput.Key == VimKey.Back || keyInput == KeyInputUtil.EnterKey) && _buffer.ModeKind != ModeKind.Insert)
            {
                // R# special cases both the Back and Enter command in various scenarios
                //
                //  - Enter is special cased in XML doc comments presumably to do custom formatting 
                //  - Enter is supressed during debugging in Exec.  Presumably this is done to avoid the annoying
                //    "Invalid ENC Edit" dialog during debugging.
                //  - Back is special cased to delete matched parens in Exec.  
                //
                // In all of these scenarios if the Enter or Back key is registered as a valid Vim
                // command we want to process it as such and prevent R# from seeing the command.  If 
                // R# is allowed to see the command they will process it often resulting in double 
                // actions
                status = CommandStatus.Enable;
                passToResharper = false;
            }

            // Only process the KeyInput if we are enabling the value.  When the value is Enabled
            // we return Enabled from QueryStatus and Visual Studio will push the KeyInput back
            // through the event chain where either of the following will happen 
            //
            //  1. R# will handle the KeyInput
            //  2. R# will not handle it, it will come back to use in Exec and we will ignore it
            //     because we mark it as silently handled
            if (status.HasValue && status.Value == CommandStatus.Enable && _buffer.Process(keyInput).IsAnyHandled)
            {
                // We've broken the rules a bit by handling the command in QueryStatus and we need
                // to silently handle this command if it comes back to us again either through 
                // Exec or through the VsKeyProcessor
                _bufferCoordinator.DiscardedKeyInput = FSharpOption.Create(keyInput);

                // If we need to cooperate with R# to handle this command go ahead and pass it on 
                // to them.  Else mark it as Disabled.
                //
                // Marking it as Disabled will cause the QueryStatus call to fail.  This means the 
                // KeyInput will be routed to the KeyProcessor chain for the ITextView eventually
                // making it to our VsKeyProcessor.  That component respects the SilentlyHandled 
                // statu of KeyInput and will siently handle it
                status = passToResharper ? CommandStatus.Enable : CommandStatus.Disable;
            }

            return status;
        }

        internal static Result<VsCommandTarget> Create(
            IVimBufferCoordinator bufferCoordinator,
            IVsTextView vsTextView,
            IVsAdapter adapter,
            IDisplayWindowBroker broker,
            IExternalEditorManager externalEditorManager)
        {
            var vsCommandTarget = new VsCommandTarget(bufferCoordinator, adapter, broker, externalEditorManager);
            var hresult = vsTextView.AddCommandFilter(vsCommandTarget, out vsCommandTarget._nextTarget);
            var result = Result.CreateSuccessOrError(vsCommandTarget, hresult);
            if (result.IsSuccess)
            {
                bufferCoordinator.VimBuffer.TextView.Properties[Key] = vsCommandTarget;
            }

            return result;
        }

        internal static bool TryGet(ITextView textView, out VsCommandTarget vsCommandTarget)
        {
            return textView.Properties.TryGetPropertySafe(Key, out vsCommandTarget);
        }
    }
}
