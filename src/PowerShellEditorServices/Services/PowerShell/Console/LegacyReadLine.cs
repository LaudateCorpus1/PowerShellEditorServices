﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.PowerShell.EditorServices.Services.PowerShell.Debugging;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Execution;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Host;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Utility;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text;
using System.Threading;

namespace Microsoft.PowerShell.EditorServices.Services.PowerShell.Console
{
    using System;

    internal class LegacyReadLine : TerminalReadLine
    {
        private readonly PsesInternalHost _psesHost;

        private readonly IPowerShellDebugContext _debugContext;

        private Func<bool, ConsoleKeyInfo> _readKeyFunc;

        public LegacyReadLine(
            PsesInternalHost psesHost,
            IPowerShellDebugContext debugContext)
        {
            _psesHost = psesHost;
            _debugContext = debugContext;
        }

        public override string ReadLine(CancellationToken cancellationToken)
        {
            // TODO: Is inputBeforeCompletion used?
            string inputBeforeCompletion = null;
            string inputAfterCompletion = null;
            CommandCompletion currentCompletion = null;

            int historyIndex = -1;
            IReadOnlyList<PSObject> currentHistory = null;

            StringBuilder inputLine = new StringBuilder();

            int initialCursorCol = ConsoleProxy.GetCursorLeft(cancellationToken);
            int initialCursorRow = ConsoleProxy.GetCursorTop(cancellationToken);

            // TODO: Are these used?
            int initialWindowLeft = Console.WindowLeft;
            int initialWindowTop = Console.WindowTop;

            int currentCursorIndex = 0;

            Console.TreatControlCAsInput = true;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ConsoleKeyInfo keyInfo = ReadKey(cancellationToken);

                    // Do final position calculation after the key has been pressed
                    // because the window could have been resized before then
                    int promptStartCol = initialCursorCol;
                    int promptStartRow = initialCursorRow;
                    int consoleWidth = Console.WindowWidth;

                    //case ConsoleKey.C when ((keyInfo.Modifiers & ConsoleModifiers.Control) != 0):
                    //  throw new PipelineStoppedException();


                    switch (keyInfo.Key)
                    {
                        case ConsoleKey.Tab:
                            if (currentCompletion == null)
                            {
                                inputBeforeCompletion = inputLine.ToString();
                                inputAfterCompletion = null;

                                // TODO: This logic should be moved to AstOperations or similar!

                                if (_debugContext.IsStopped)
                                {
                                    PSCommand command = new PSCommand()
                                        .AddCommand("TabExpansion2")
                                        .AddParameter("InputScript", inputBeforeCompletion)
                                        .AddParameter("CursorColumn", currentCursorIndex)
                                        .AddParameter("Options", null);

                                    currentCompletion = _psesHost.InvokePSCommand<CommandCompletion>(command, PowerShellExecutionOptions.Default, cancellationToken).FirstOrDefault();
                                }
                                else
                                {
                                    currentCompletion = _psesHost.InvokePSDelegate(
                                        "Legacy readline inline command completion",
                                        ExecutionOptions.Default,
                                        (pwsh, cancellationToken) => CommandCompletion.CompleteInput(inputAfterCompletion, currentCursorIndex, options: null, pwsh),
                                        cancellationToken);

                                    if (currentCompletion.CompletionMatches.Count > 0)
                                    {
                                        int replacementEndIndex =
                                                currentCompletion.ReplacementIndex +
                                                currentCompletion.ReplacementLength;

                                        inputAfterCompletion =
                                            inputLine.ToString(
                                                replacementEndIndex,
                                                inputLine.Length - replacementEndIndex);
                                    }
                                    else
                                    {
                                        currentCompletion = null;
                                    }
                                }
                            }

                            CompletionResult completion =
                                currentCompletion?.GetNextResult(
                                    !keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift));

                            if (completion != null)
                            {
                                currentCursorIndex =
                                    InsertInput(
                                        inputLine,
                                        promptStartCol,
                                        promptStartRow,
                                        $"{completion.CompletionText}{inputAfterCompletion}",
                                        currentCursorIndex,
                                        insertIndex: currentCompletion.ReplacementIndex,
                                        replaceLength: inputLine.Length - currentCompletion.ReplacementIndex,
                                        finalCursorIndex: currentCompletion.ReplacementIndex + completion.CompletionText.Length);
                            }

                            continue;

                        case ConsoleKey.LeftArrow:
                            currentCompletion = null;

                            if (currentCursorIndex > 0)
                            {
                                currentCursorIndex =
                                    MoveCursorToIndex(
                                        promptStartCol,
                                        promptStartRow,
                                        consoleWidth,
                                        currentCursorIndex - 1);
                            }

                            continue;

                        case ConsoleKey.Home:
                            currentCompletion = null;

                            currentCursorIndex =
                                MoveCursorToIndex(
                                    promptStartCol,
                                    promptStartRow,
                                    consoleWidth,
                                    0);

                            continue;

                        case ConsoleKey.RightArrow:
                            currentCompletion = null;

                            if (currentCursorIndex < inputLine.Length)
                            {
                                currentCursorIndex =
                                    MoveCursorToIndex(
                                        promptStartCol,
                                        promptStartRow,
                                        consoleWidth,
                                        currentCursorIndex + 1);
                            }

                            continue;

                        case ConsoleKey.End:
                            currentCompletion = null;

                            currentCursorIndex =
                                MoveCursorToIndex(
                                    promptStartCol,
                                    promptStartRow,
                                    consoleWidth,
                                    inputLine.Length);

                            continue;

                        case ConsoleKey.UpArrow:
                            currentCompletion = null;

                            // TODO: Ctrl+Up should allow navigation in multi-line input

                            if (currentHistory == null)
                            {
                                historyIndex = -1;

                                PSCommand command = new PSCommand()
                                    .AddCommand("Get-History");

                                currentHistory = _psesHost.InvokePSCommand<PSObject>(command, PowerShellExecutionOptions.Default, cancellationToken);

                                if (currentHistory != null)
                                {
                                    historyIndex = currentHistory.Count;
                                }
                            }

                            if (currentHistory != null && currentHistory.Count > 0 && historyIndex > 0)
                            {
                                historyIndex--;

                                currentCursorIndex =
                                    InsertInput(
                                        inputLine,
                                        promptStartCol,
                                        promptStartRow,
                                        (string)currentHistory[historyIndex].Properties["CommandLine"].Value,
                                        currentCursorIndex,
                                        insertIndex: 0,
                                        replaceLength: inputLine.Length);
                            }

                            continue;

                        case ConsoleKey.DownArrow:
                            currentCompletion = null;

                            // The down arrow shouldn't cause history to be loaded,
                            // it's only for navigating an active history array

                            if (historyIndex > -1 && historyIndex < currentHistory.Count &&
                                currentHistory != null && currentHistory.Count > 0)
                            {
                                historyIndex++;

                                if (historyIndex < currentHistory.Count)
                                {
                                    currentCursorIndex =
                                        InsertInput(
                                            inputLine,
                                            promptStartCol,
                                            promptStartRow,
                                            (string)currentHistory[historyIndex].Properties["CommandLine"].Value,
                                            currentCursorIndex,
                                            insertIndex: 0,
                                            replaceLength: inputLine.Length);
                                }
                                else if (historyIndex == currentHistory.Count)
                                {
                                    currentCursorIndex =
                                        InsertInput(
                                            inputLine,
                                            promptStartCol,
                                            promptStartRow,
                                            string.Empty,
                                            currentCursorIndex,
                                            insertIndex: 0,
                                            replaceLength: inputLine.Length);
                                }
                            }

                            continue;

                        case ConsoleKey.Escape:
                            currentCompletion = null;
                            historyIndex = currentHistory != null ? currentHistory.Count : -1;

                            currentCursorIndex =
                                InsertInput(
                                    inputLine,
                                    promptStartCol,
                                    promptStartRow,
                                    string.Empty,
                                    currentCursorIndex,
                                    insertIndex: 0,
                                    replaceLength: inputLine.Length);

                            continue;

                        case ConsoleKey.Backspace:
                            currentCompletion = null;

                            if (currentCursorIndex > 0)
                            {
                                currentCursorIndex =
                                    InsertInput(
                                        inputLine,
                                        promptStartCol,
                                        promptStartRow,
                                        string.Empty,
                                        currentCursorIndex,
                                        insertIndex: currentCursorIndex - 1,
                                        replaceLength: 1,
                                        finalCursorIndex: currentCursorIndex - 1);
                            }

                            continue;

                        case ConsoleKey.Delete:
                            currentCompletion = null;

                            if (currentCursorIndex < inputLine.Length)
                            {
                                currentCursorIndex =
                                    InsertInput(
                                        inputLine,
                                        promptStartCol,
                                        promptStartRow,
                                        string.Empty,
                                        currentCursorIndex,
                                        replaceLength: 1,
                                        finalCursorIndex: currentCursorIndex);
                            }

                            continue;

                        case ConsoleKey.Enter:
                            string completedInput = inputLine.ToString();
                            currentCompletion = null;
                            currentHistory = null;

                            //if ((keyInfo.Modifiers & ConsoleModifiers.Shift) == ConsoleModifiers.Shift)
                            //{
                            //    // TODO: Start a new line!
                            //    continue;
                            //}

                            Parser.ParseInput(
                                completedInput,
                                out Token[] tokens,
                                out ParseError[] parseErrors);

                            //if (parseErrors.Any(e => e.IncompleteInput))
                            //{
                            //    // TODO: Start a new line!
                            //    continue;
                            //}

                            return completedInput;

                        default:
                            if (keyInfo.IsCtrlC())
                            {
                                throw new PipelineStoppedException();
                            }

                            // Normal character input
                            if (keyInfo.KeyChar != 0 && !char.IsControl(keyInfo.KeyChar))
                            {
                                currentCompletion = null;

                                currentCursorIndex =
                                    InsertInput(
                                        inputLine,
                                        promptStartCol,
                                        promptStartRow,
                                        keyInfo.KeyChar.ToString(), // TODO: Determine whether this should take culture into account
                                        currentCursorIndex,
                                        finalCursorIndex: currentCursorIndex + 1);
                            }

                            continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // We've broken out of the loop
            }
            finally
            {
                Console.TreatControlCAsInput = false;
            }

            // If we break out of the loop without returning (because of the Enter key)
            // then the readline has been aborted in some way and we should return nothing
            return null;
        }

        public override bool TryOverrideIdleHandler(Action<CancellationToken> idleHandler)
        {
            return true;
        }

        public override bool TryOverrideReadKey(Func<bool, ConsoleKeyInfo> readKeyOverride)
        {
            _readKeyFunc = readKeyOverride;
            return true;
        }

        protected override ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // intercept = false means we display the key in the console
                return _readKeyFunc(/* intercept */ false);
            }
            finally
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private static int InsertInput(
            StringBuilder inputLine,
            int promptStartCol,
            int promptStartRow,
            string insertedInput,
            int cursorIndex,
            int insertIndex = -1,
            int replaceLength = 0,
            int finalCursorIndex = -1)
        {
            int consoleWidth = Console.WindowWidth;
            int previousInputLength = inputLine.Length;

            if (insertIndex == -1)
            {
                insertIndex = cursorIndex;
            }

            // Move the cursor to the new insertion point
            MoveCursorToIndex(
                promptStartCol,
                promptStartRow,
                consoleWidth,
                insertIndex);

            // Edit the input string based on the insertion
            if (insertIndex < inputLine.Length)
            {
                if (replaceLength > 0)
                {
                    inputLine.Remove(insertIndex, replaceLength);
                }

                inputLine.Insert(insertIndex, insertedInput);
            }
            else
            {
                inputLine.Append(insertedInput);
            }

            // Re-render affected section
            Console.Write(
                inputLine.ToString(
                    insertIndex,
                    inputLine.Length - insertIndex));

            if (inputLine.Length < previousInputLength)
            {
                Console.Write(
                    new string(
                        ' ',
                        previousInputLength - inputLine.Length));
            }

            // Automatically set the final cursor position to the end
            // of the new input string.  This is needed if the previous
            // input string is longer than the new one and needed to have
            // its old contents overwritten.  This will position the cursor
            // back at the end of the new text
            if (finalCursorIndex == -1 && inputLine.Length < previousInputLength)
            {
                finalCursorIndex = inputLine.Length;
            }

            if (finalCursorIndex > -1)
            {
                // Move the cursor to the final position
                return MoveCursorToIndex(
                    promptStartCol,
                    promptStartRow,
                    consoleWidth,
                    finalCursorIndex);
            }
            else
            {
                return inputLine.Length;
            }
        }

        private static int MoveCursorToIndex(
            int promptStartCol,
            int promptStartRow,
            int consoleWidth,
            int newCursorIndex)
        {
            CalculateCursorFromIndex(
                promptStartCol,
                promptStartRow,
                consoleWidth,
                newCursorIndex,
                out int newCursorCol,
                out int newCursorRow);

            Console.SetCursorPosition(newCursorCol, newCursorRow);

            return newCursorIndex;
        }
        private static void CalculateCursorFromIndex(
            int promptStartCol,
            int promptStartRow,
            int consoleWidth,
            int inputIndex,
            out int cursorCol,
            out int cursorRow)
        {
            cursorCol = promptStartCol + inputIndex;
            cursorRow = promptStartRow + cursorCol / consoleWidth;
            cursorCol = cursorCol % consoleWidth;
        }
    }
}