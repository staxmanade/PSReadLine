﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PSConsoleUtilities
{
    public enum EditMode
    {
        Windows,
        Emacs,
#if FALSE
        Vi,
#endif
    }

    public enum BellStyle
    {
        None,
        Visual,
        Audible
    }

    public enum TokenClassification
    {
        None,
        Comment,
        Keyword,
        String,
        Operator,
        Variable,
        Command,
        Parameter,
        Type,
        Number,
        Member,
    }

    public class KeyHandler
    {
        public string Key { get; set; }
        public string BriefDescription { get; set; }
        public string LongDescription
        {
            get
            {
                var result = _longDescription;
                if (string.IsNullOrWhiteSpace(result))
                    result = PSReadLineResources.ResourceManager.GetString(BriefDescription + "Description");
                if (string.IsNullOrWhiteSpace(result))
                    result = BriefDescription;
                return result;
            }
            set { _longDescription = value; }
        }
        private string _longDescription;
    }

    public class PSConsoleReadLine
    {
        private static readonly PSConsoleReadLine _singleton;

        private static readonly GCHandle _breakHandlerGcHandle;
        private Thread _readKeyThread;
        private AutoResetEvent _readKeyWaitHandle;
        private AutoResetEvent _keyReadWaitHandle;
        private AutoResetEvent _closingWaitHandle;
        private WaitHandle[] _waitHandles;
        private bool _captureKeys;
        private readonly Queue<ConsoleKeyInfo> _savedKeys; 
        private readonly HistoryQueue<string> _demoStrings;
        private bool _demoMode;
        private int _demoWindowLineCount;
        private bool _renderForDemoNeeded;

        class KeyHandler
        {
            // Each key handler will be passed 2 arguments.  Most will ignore these arguments,
            // but having a consistent signature greatly simplifies dispatch.  Defaults
            // should be included on all handlers that ignore their parameters so they
            // can be called from PowerShell without passing anything.
            //
            // The first arugment is the key that caused the action to be called
            // (the second key when it's a 2 key chord).  The default is null (it's nullable)
            // because PowerShell can't handle default(ConsoleKeyInfo) as a default.
            // Most actions will ignore this argument.
            //
            // The second argument is an arbitrary object.  It will usually be either a number
            // (e.g. as a repeat count) or a string.  Most actions will ignore this argument.
            public Action<ConsoleKeyInfo?, object> Action;
            public string BriefDescription;
            public string LongDescription;
        }

        [DebuggerDisplay("{_line}")]
        class HistoryItem
        {
            public string _line;
            public List<EditItem> _edits;
            public int _undoEditIndex;
        }

        static KeyHandler MakeKeyHandler(Action<ConsoleKeyInfo?, object> action, string briefDescription, string longDescription = null)
        {
            return new KeyHandler
            {
                Action = action,
                BriefDescription = briefDescription,
                LongDescription = longDescription
            };
        }

        private static readonly Dictionary<ConsoleKeyInfo, KeyHandler> _emacsKeyMap; 
        private static readonly Dictionary<ConsoleKeyInfo, KeyHandler> _emacsMetaMap; 
        private static readonly Dictionary<ConsoleKeyInfo, KeyHandler> _emacsCtrlXMap; 
        private static readonly Dictionary<ConsoleKeyInfo, KeyHandler> _cmdKeyMap;

        private Dictionary<ConsoleKeyInfo, KeyHandler> _dispatchTable;
        private Dictionary<ConsoleKeyInfo, Dictionary<ConsoleKeyInfo, KeyHandler>> _chordDispatchTable; 
        private Dictionary<ConsoleKeyInfo, KeyHandler> _dispatchCtrlXTable;
        private Dictionary<ConsoleKeyInfo, KeyHandler> _dispatchMetaTable;

        private readonly StringBuilder _buffer;
        private List<EditItem> _edits;
        private int _editGroupCount;
        private readonly Stack<int> _pushedEditGroupCount;
        private int _undoEditIndex;
        private CHAR_INFO[] _consoleBuffer;
        private int _current;
        private int _mark;
        private bool _inputAccepted;
        private int _initialX;
        private int _initialY;
        private int _bufferWidth;
        private ConsoleColor _initialBackgroundColor;
        private ConsoleColor _initialForegroundColor;
        private CHAR_INFO _space;
        private readonly Queue<ConsoleKeyInfo> _queuedKeys;
        private DateTime _lastRenderTime;

        // History state
        private HistoryQueue<HistoryItem> _history;
        private readonly HashSet<string> _hashedHistory; 
        private int _currentHistoryIndex;
        private int _searchHistoryCommandCount;
        private string _searchHistoryPrefix;
        // When cycling through history, the current line (not yet added to history)
        // is saved here so it can be restored.
        private HistoryItem _savedCurrentLine;

        // Yank/Kill state
        private readonly List<string> _killRing;
        private int _killIndex;
        private int _killCommandCount;
        private int _yankCommandCount;
        private int _yankStartPoint;

        // Tab completion state
        private int _tabCommandCount;
        private CommandCompletion _tabCompletions;

        // Tokens etc.
        private Token[] _tokens;
        private Ast _ast;
        private ParseError[] _parseErrors;

#region Configuration options

        private readonly PSConsoleReadlineOptions _options;

        #endregion Configuration options

        #region Unit test only properties

        // These properties exist solely so the Fakes assembly has something
        // that can be used to access the private bits here.  It's annoying
        // to be so close to 100% coverage and not have 100% coverage!
        private CHAR_INFO[] ConsoleBuffer { get { return _consoleBuffer; } }

        public PSConsoleReadlineOptions Options
        {
            get { return _options; }
        }

        #endregion Unit test only properties

        private void ReadKeyThreadProc()
        {
            while (true)
            {
                // Wait until ReadKey tells us to read a key.
                _readKeyWaitHandle.WaitOne();

                var start = DateTime.Now;
                while (Console.KeyAvailable)
                {
                    _queuedKeys.Enqueue(Console.ReadKey(true));
                    if ((DateTime.Now - start).Milliseconds > 2)
                    {
                        // Don't spend too long in this loop if there are lots of queued keys
                        break;
                    }
                }

                var key = _queuedKeys.Count > 0
                    ? _queuedKeys.Dequeue()
                    : Console.ReadKey(true);
                if (_captureKeys)
                {
                    _savedKeys.Enqueue(key);
                }

                _queuedKeys.Enqueue(key);

                // One or more keys were read - let ReadKey know we're done.
                _keyReadWaitHandle.Set();
            }
        }

        [ExcludeFromCodeCoverage]
        private static ConsoleKeyInfo ReadKey()
        {
            // Reading a key is handled on a different thread.  During process shutdown,
            // PowerShell will wait in it's ConsoleCtrlHandler until the pipeline has completed.
            // If we're running, we're most likely blocked waiting for user input.
            // This is a problem for two reasons.  First, exiting takes a long time (5 seconds
            // on Win8) because PowerShell is waiting forever, but the OS will forcibly terminate
            // the console.  Also - if there are any event handlers for the engine event
            // PowerShell.Exiting, those handlers won't get a chance to run.
            //
            // By waiting for a key on a different thread, our pipeline execution thread
            // (the thread Readline is called from) avoid being blocked in code that can't
            // be unblocked and instead blocks on events we control.

            // First, set an event so the thread to read a key actually attempts to read a key.
            _singleton._readKeyWaitHandle.Set();

            // Next, wait for one of two things - either a key is pressed on the console is exiting.
            int handleId = WaitHandle.WaitAny(_singleton._waitHandles);
            if (handleId == 1)
            {
                // The console is exiting - throw an exception to unwind the stack to the point
                // where we can return from ReadLine.
                throw new OperationCanceledException();
            }
            return _singleton._queuedKeys.Dequeue();
        }

        private bool BreakHandler(ConsoleBreakSignal signal)
        {
            if (signal == ConsoleBreakSignal.Close || signal == ConsoleBreakSignal.Shutdown)
            {
                // Set the event so ReadKey throws an exception to unwind.
                _singleton._closingWaitHandle.Set();
            }

            return false;
        }

        /// <summary>
        /// Entry point - called from the PowerShell function PSConsoleHostReadline
        /// after the prompt has been displayed.
        /// </summary>
        /// <returns>The complete command line.</returns>
        public static string ReadLine()
        {
            uint dwConsoleMode;
            var handle = NativeMethods.GetStdHandle((uint) StandardHandleId.Input);
            NativeMethods.GetConsoleMode(handle, out dwConsoleMode);
            try
            {
                NativeMethods.SetConsoleMode(handle, dwConsoleMode & ~NativeMethods.ENABLE_PROCESSED_INPUT);

                _singleton.Initialize();
                return _singleton.InputLoop();
            }
            catch (OperationCanceledException)
            {
                // Console is exiting - return value isn't too critical - null or 'exit' could work equally well.
                return "";
            }
            finally
            {
                NativeMethods.SetConsoleMode(handle, dwConsoleMode);
            }
        }

        private string InputLoop()
        {
            while (true)
            {
                var killCommandCount = _killCommandCount;
                var yankCommandCount = _yankCommandCount;
                var tabCommandCount = _tabCommandCount;
                var searchHistoryCommandCount = _searchHistoryCommandCount;

                var key = ReadKey();
                ProcessOneKey(key, _dispatchTable, ignoreIfNoAction: false);
                if (_inputAccepted)
                {
                    return MaybeAddToHistory(_buffer.ToString(), _edits, _undoEditIndex);
                }

                if (killCommandCount == _killCommandCount)
                {
                    // Reset kill command count if it didn't change
                    _killCommandCount = 0;
                }
                if (yankCommandCount == _yankCommandCount)
                {
                    // Reset yank command count if it didn't change
                    _yankCommandCount = 0;
                }
                if (tabCommandCount == _tabCommandCount)
                {
                    // Reset tab command count if it didn't change
                    _tabCommandCount = 0;
                    _tabCompletions = null;
                }
                if (searchHistoryCommandCount == _searchHistoryCommandCount)
                {
                    _searchHistoryCommandCount = 0;
                    _searchHistoryPrefix = null;
                }
            }
        }

        // Test hook.
        [ExcludeFromCodeCoverage]
        private static void PostKeyHandler()
        {
        }

        void ProcessOneKey(ConsoleKeyInfo key, Dictionary<ConsoleKeyInfo, KeyHandler> dispatchTable, bool ignoreIfNoAction)
        {
            KeyHandler handler;
            if (dispatchTable.TryGetValue(key, out handler))
            {
                _renderForDemoNeeded = _demoMode;

                handler.Action(key, 0);

                if (_renderForDemoNeeded)
                {
                    Render();
                }
            }
            else if (!ignoreIfNoAction && key.KeyChar != 0)
            {
                Insert(key.KeyChar);
            }
            PostKeyHandler();
        }

        private string MaybeAddToHistory(string result, List<EditItem> edits, int undoEditIndex)
        {
            bool addToHistory = !string.IsNullOrWhiteSpace(result) && ((Options.AddToHistoryHandler == null) || Options.AddToHistoryHandler(result));
            if (addToHistory && Options.HistoryNoDuplicates)
            {
                // REVIEW: should history be case sensitive - it is now.
                // A smart comparer could use the ast to ignore case on commands, parameters,
                // operators and keywords while remaining case sensitive on command arguments.
                addToHistory = !_hashedHistory.Contains(result);
            }
            if (addToHistory)
            {
                _history.Enqueue(new HistoryItem
                {
                    _line = result,
                    _edits = edits,
                    _undoEditIndex = undoEditIndex
                });
                _currentHistoryIndex = _history.Count;
            }
            if (_demoMode)
            {
                ClearDemoWindow();
            }
            return result;
        }

        private void HistoryOnEnqueueHandler(HistoryItem obj)
        {
            if (Options.HistoryNoDuplicates)
            {
                _hashedHistory.Add(obj._line);
            }
        }

        private void HistoryOnDequeueHandler(HistoryItem obj)
        {
            if (Options.HistoryNoDuplicates)
            {
                _hashedHistory.Remove(obj._line);
            }
        }

        static PSConsoleReadLine()
        {
            _cmdKeyMap = new Dictionary<ConsoleKeyInfo, KeyHandler>(new ConsoleKeyInfoComparer())
            {
                { Keys.Enter,           MakeKeyHandler(AcceptLine,           "AcceptLine") },
                { Keys.ShiftEnter,      MakeKeyHandler(AddLine,              "AddLine") },
                { Keys.Escape,          MakeKeyHandler(RevertLine,           "RevertLine") },
                { Keys.LeftArrow,       MakeKeyHandler(BackwardChar,         "BackwardChar") },
                { Keys.RightArrow,      MakeKeyHandler(ForwardChar,          "ForwardChar") },
                { Keys.CtrlLeftArrow,   MakeKeyHandler(BackwardWord,         "BackwordWord") },
                { Keys.CtrlRightArrow,  MakeKeyHandler(ForwardWord,          "ForwardWord") },
                { Keys.UpArrow,         MakeKeyHandler(PreviousHistory,      "PreviousHistory") },
                { Keys.DownArrow,       MakeKeyHandler(NextHistory,          "NextHistory") },
                { Keys.Home,            MakeKeyHandler(BeginningOfLine,      "BeginningOfLine") },
                { Keys.End,             MakeKeyHandler(EndOfLine,            "EndOfLine") },
                { Keys.Delete,          MakeKeyHandler(DeleteChar,           "DeleteChar") },
                { Keys.Backspace,       MakeKeyHandler(BackwardDeleteChar,   "BackwardDeleteChar") },
                { Keys.CtrlSpace,       MakeKeyHandler(PossibleCompletions,  "PossibleCompletions") },
                { Keys.Tab,             MakeKeyHandler(TabCompleteNext,      "TabCompleteNext") },
                { Keys.ShiftTab,        MakeKeyHandler(TabCompletePrevious,  "TabCompletePrevious") },
                { Keys.CtrlV,           MakeKeyHandler(Paste,                "Paste") },
                { Keys.VolumeDown,      MakeKeyHandler(Ignore,               "Ignore") },
                { Keys.VolumeUp,        MakeKeyHandler(Ignore,               "Ignore") },
                { Keys.VolumeMute,      MakeKeyHandler(Ignore,               "Ignore") },
                { Keys.CtrlC,           MakeKeyHandler(CancelLine,           "CancelLine") },
                { Keys.CtrlY,           MakeKeyHandler(Redo,                 "Redo") },
                { Keys.CtrlZ,           MakeKeyHandler(Undo,                 "Undo") },
                { Keys.CtrlEnd,         MakeKeyHandler(ForwardDeleteLine,    "ForwardDeleteLine") },
                { Keys.CtrlHome,        MakeKeyHandler(BackwardDeleteLine,   "BackwardDeleteLine") },
                { Keys.CtrlRBracket,    MakeKeyHandler(GotoBrace,            "GotoBrace") },
            };

            _emacsKeyMap = new Dictionary<ConsoleKeyInfo, KeyHandler>(new ConsoleKeyInfoComparer())
            {
                { Keys.Backspace,       MakeKeyHandler(BackwardDeleteChar,   "BackwardDeleteChar") },
                { Keys.Enter,           MakeKeyHandler(AcceptLine,           "AcceptLine") },
                { Keys.ShiftEnter,      MakeKeyHandler(AddLine,              "AddLine") },
                { Keys.LeftArrow,       MakeKeyHandler(BackwardChar,         "BackwardChar") },
                { Keys.RightArrow,      MakeKeyHandler(ForwardChar,          "ForwardChar") },
                { Keys.UpArrow,         MakeKeyHandler(PreviousHistory,      "PreviousHistory") },
                { Keys.DownArrow,       MakeKeyHandler(NextHistory,          "NextHistory") },
                { Keys.AltLess,         MakeKeyHandler(BeginningOfHistory,   "BeginningOfHistory") },
                { Keys.AltGreater,      MakeKeyHandler(EndOfHistory,         "EndOfHistory") },
                { Keys.Home,            MakeKeyHandler(BeginningOfLine,      "BeginningOfLine") },
                { Keys.End,             MakeKeyHandler(EndOfLine,            "EndOfLine") },
                { Keys.Escape,          MakeKeyHandler(Chord,                "ChordFirstKey") },
                { Keys.Delete,          MakeKeyHandler(DeleteChar,           "DeleteChar") },
                { Keys.Tab,             MakeKeyHandler(Complete,             "Complete") },
                { Keys.CtrlA,           MakeKeyHandler(BeginningOfLine,      "BeginningOfLine") },
                { Keys.CtrlB,           MakeKeyHandler(BackwardChar,         "BackwardChar") },
                { Keys.CtrlC,           MakeKeyHandler(CancelLine,           "CancelLine") },
                { Keys.CtrlD,           MakeKeyHandler(DeleteChar,           "DeleteChar") },
                { Keys.CtrlE,           MakeKeyHandler(EndOfLine,            "EndOfLine") },
                { Keys.CtrlF,           MakeKeyHandler(ForwardChar,          "ForwardChar") },
                { Keys.CtrlH,           MakeKeyHandler(BackwardDeleteChar,   "BackwardDeleteChar") },
                { Keys.CtrlK,           MakeKeyHandler(KillLine,             "KillLine") },
                { Keys.CtrlM,           MakeKeyHandler(AcceptLine,           "AcceptLine") },
                { Keys.CtrlU,           MakeKeyHandler(BackwardKillLine,     "BackwardKillLine") },
                { Keys.CtrlX,           MakeKeyHandler(Chord,                "ChordFirstKey") },
                { Keys.CtrlY,           MakeKeyHandler(Yank,                 "Yank") },
                { Keys.CtrlAt,          MakeKeyHandler(SetMark,              "SetMark") },
                { Keys.CtrlUnderbar,    MakeKeyHandler(Undo,                 "Undo") },
                { Keys.AltB,            MakeKeyHandler(EmacsBackwardWord,    "EmacsBackwardWord") },
                { Keys.AltD,            MakeKeyHandler(KillWord,             "KillWord") },
                { Keys.AltF,            MakeKeyHandler(EmacsForwardWord,     "EmacsForwardWord") },
                { Keys.AltR,            MakeKeyHandler(RevertLine,           "RevertLine") },
                { Keys.AltY,            MakeKeyHandler(YankPop,              "YankPop") },
                { Keys.AltBackspace,    MakeKeyHandler(KillBackwardWord,     "KillBackwardWord") },
                { Keys.AltEquals,       MakeKeyHandler(PossibleCompletions,  "PossibleCompletions") },
                { Keys.AltSpace,        MakeKeyHandler(SetMark,              "SetMark") },  // useless entry here for completeness - brings up system menu on Windows
                { Keys.VolumeDown,      MakeKeyHandler(Ignore,               "Ignore") },
                { Keys.VolumeUp,        MakeKeyHandler(Ignore,               "Ignore") },
                { Keys.VolumeMute,      MakeKeyHandler(Ignore,               "Ignore") },
            };

            _emacsMetaMap = new Dictionary<ConsoleKeyInfo, KeyHandler>(new ConsoleKeyInfoComparer())
            {
                { Keys.B,               MakeKeyHandler(EmacsBackwardWord,    "EmacsBackwardWord") },
                { Keys.D,               MakeKeyHandler(KillWord,             "KillWord") },
                { Keys.F,               MakeKeyHandler(EmacsForwardWord,     "EmacsForwardWord") },
                { Keys.R,               MakeKeyHandler(RevertLine,           "RevertLine") },
                { Keys.Y,               MakeKeyHandler(YankPop,              "YankPop") },
                { Keys.Backspace,       MakeKeyHandler(KillBackwardWord,     "KillBackwardWord") },
            };

            _emacsCtrlXMap = new Dictionary<ConsoleKeyInfo, KeyHandler>(new ConsoleKeyInfoComparer())
            {
                { Keys.Backspace,       MakeKeyHandler(BackwardKillLine,     "BackwardKillLine") },
                { Keys.CtrlU,           MakeKeyHandler(Undo,                 "Undo") },
                { Keys.CtrlX,           MakeKeyHandler(ExchangePointAndMark, "ExchangePointAndMark") },
            };

            _singleton = new PSConsoleReadLine();

            _breakHandlerGcHandle = GCHandle.Alloc(new BreakHandler(_singleton.BreakHandler));
            NativeMethods.SetConsoleCtrlHandler((BreakHandler) _breakHandlerGcHandle.Target, true);
            _singleton._readKeyThread = new Thread(_singleton.ReadKeyThreadProc);
            _singleton._readKeyThread.Start();
            _singleton._readKeyWaitHandle = new AutoResetEvent(false);
            _singleton._keyReadWaitHandle = new AutoResetEvent(false);
            _singleton._closingWaitHandle = new AutoResetEvent(false);
            _singleton._waitHandles = new WaitHandle[] { _singleton._keyReadWaitHandle, _singleton._closingWaitHandle };
        }

        private PSConsoleReadLine()
        {
            _captureKeys = false;
            _savedKeys = new Queue<ConsoleKeyInfo>();
            _demoStrings = new HistoryQueue<string>(100);
            _demoMode = false;

            _dispatchTable = new Dictionary<ConsoleKeyInfo, KeyHandler>(_cmdKeyMap);
            _chordDispatchTable = new Dictionary<ConsoleKeyInfo, Dictionary<ConsoleKeyInfo, KeyHandler>>();

            _buffer = new StringBuilder(8 * 1024);
            _savedCurrentLine = new HistoryItem();
            _queuedKeys = new Queue<ConsoleKeyInfo>();

            _pushedEditGroupCount = new Stack<int>();

            _options = new PSConsoleReadlineOptions();

            _history = new HistoryQueue<HistoryItem>(Options.MaximumHistoryCount)
            {
                OnDequeue = HistoryOnDequeueHandler,
                OnEnqueue = HistoryOnEnqueueHandler
            };
            _currentHistoryIndex = 0;
            _hashedHistory = new HashSet<string>();

            _killIndex = -1;    // So first add indexes 0.
            _killRing = new List<string>(Options.MaximumKillRingCount);
        }

        private void Initialize()
        {
            _buffer.Clear();
            _edits = new List<EditItem>();
            _undoEditIndex = 0;
            _editGroupCount = 0;
            _pushedEditGroupCount.Clear();
            _current = 0;
            _mark = 0;
            _tokens = null;
            _parseErrors = null;
            _inputAccepted = false;
            _initialX = Console.CursorLeft;
            _initialY = Console.CursorTop - Options.ExtraPromptLineCount;
            _initialBackgroundColor = Console.BackgroundColor;
            _initialForegroundColor = Console.ForegroundColor;
            _space = new CHAR_INFO(' ', _initialForegroundColor, _initialBackgroundColor);
            _bufferWidth = Console.BufferWidth;
            _killCommandCount = 0;
            _yankCommandCount = 0;
            _tabCommandCount = 0;

            _consoleBuffer = ReadBufferLines(_initialY, 1 + Options.ExtraPromptLineCount);
            _lastRenderTime = DateTime.Now;
        }

        private static void Chord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!key.HasValue)
            {
                throw new ArgumentNullException("key");
            }
            Dictionary<ConsoleKeyInfo, KeyHandler> secondKeyDispatchTable;
            if (_singleton._chordDispatchTable.TryGetValue(key.Value, out secondKeyDispatchTable))
            {
                if (_singleton._demoMode)
                {
                    // Render so the first key of the chord appears in the demo window
                    _singleton.Render();
                }
                var secondKey = ReadKey();
                _singleton.ProcessOneKey(secondKey, secondKeyDispatchTable, ignoreIfNoAction: true);
            }
        }

        private static void Ignore(ConsoleKeyInfo? key = null, object arg = null)
        {
        }

        /// <summary>
        /// Reverts all of the input to the current input.
        /// </summary>
        public static void RevertLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            while (_singleton._undoEditIndex > 0)
            {
                _singleton._edits[_singleton._undoEditIndex - 1].Undo(_singleton);
                _singleton._undoEditIndex--;
            }
            _singleton.Render();
        }

        /// <summary>
        /// Cancel the current input, leaving the input on the screen,
        /// but returns back to the host so the prompt is evaluated again.
        /// </summary>
        public static void CancelLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            // Append the key that canceled input and display it so we have some visual
            // hint that the command didn't run.
            _singleton._current = _singleton._buffer.Length;
            _singleton._buffer.Append(key.HasValue ? key.Value.KeyChar : Keys.CtrlC.KeyChar);
            _singleton.Render();
            Console.Out.Write("\n");
            _singleton._buffer.Clear(); // Clear so we don't actually run the input
            _singleton._inputAccepted = true;
        }

        /// <summary>
        /// Like ForwardKillLine - deletes text from the point to the end of the line,
        /// but does not put the deleted text in the kill ring.
        /// </summary>
        public static void ForwardDeleteLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            var current = _singleton._current;
            var buffer = _singleton._buffer;
            if (buffer.Length > 0 && current < buffer.Length)
            {
                int length = buffer.Length - current;
                var str = buffer.ToString(current, length);
                _singleton.SaveEditItem(EditItemDelete.Create(str, current));
                buffer.Remove(current, length);
                _singleton.Render();
            }
        }

        /// <summary>
        /// Like BackwardKillLine - deletes text from the point to the start of the line,
        /// but does not put the deleted text in the kill ring.
        /// </summary>
        public static void BackwardDeleteLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._current > 0)
            {
                var str = _singleton._buffer.ToString(0, _singleton._current);
                _singleton.SaveEditItem(EditItemDelete.Create(str, 0));
                _singleton._buffer.Remove(0, _singleton._current);
                _singleton._current = 0;
                _singleton.Render();
            }
        }

        /// <summary>
        /// Delete the character before the cursor.
        /// </summary>
        public static void BackwardDeleteChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._buffer.Length > 0 && _singleton._current > 0)
            {
                int startDeleteIndex = _singleton._current - 1;
                _singleton.SaveEditItem(
                    EditItemDelete.Create(new string(_singleton._buffer[startDeleteIndex], 1), startDeleteIndex));
                _singleton._buffer.Remove(startDeleteIndex, 1);
                _singleton._current--;
                _singleton.Render();
            }
        }

        /// <summary>
        /// Delete the character under the cursor.
        /// </summary>
        public static void DeleteChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._buffer.Length > 0 && _singleton._current < _singleton._buffer.Length)
            {
                _singleton.SaveEditItem(
                    EditItemDelete.Create(new string(_singleton._buffer[_singleton._current], 1), _singleton._current));
                _singleton._buffer.Remove(_singleton._current, 1);
                _singleton.Render();
            }
        }

        /// <summary>
        /// Attempt to execute the current input.  If the current input is incomplete (for
        /// example there is a missing closing parenthesis, bracket, or quote, then the
        /// continuation prompt is displayed on the next line and PSReadline waits for
        /// keys to edit the current input.
        /// </summary>
        public static void AcceptLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.ParseInput();
            if (_singleton._parseErrors.Any(e => e.IncompleteInput))
            {
                Insert('\n');
                return;
            }

            _singleton._renderForDemoNeeded = false;

            // Make sure cursor is at the end before writing the line
            _singleton._current = _singleton._buffer.Length;
            if (_singleton._queuedKeys.Count > 0)
            {
                // If text was pasted, for performance reasons we skip rendering for some time,
                // but if input is accepted, we won't have another chance to render.
                _singleton.ReallyRender();
            }
            _singleton.PlaceCursor();
            Console.Out.Write("\n");
            _singleton._inputAccepted = true;
        }

        /// <summary>
        /// The continuation prompt is displayed on the next line and PSReadline waits for
        /// keys to edit the current input.  This is useful to enter multi-line input as
        /// a single command even when a single line is complete input by itself.
        /// </summary>
        public static void AddLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            Insert('\n');
        }

        /// <summary>
        /// Paste text from the system clipboard.
        /// </summary>
        public static void Paste(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                string textToPaste = System.Windows.Clipboard.GetText();
                textToPaste = textToPaste.Replace("\r", "");
                _singleton._buffer.Insert(_singleton._current, textToPaste);
                _singleton._current += textToPaste.Length;
                _singleton.Render();
            }
        }

#region Movement

        /// <summary>
        /// Move the cursor to the end of the input.
        /// </summary>
        public static void EndOfLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.MoveCursor(_singleton._buffer.Length);
        }

        /// <summary>
        /// Move the cursor to the end of the input.
        /// </summary>
        public static void BeginningOfLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.MoveCursor(0);
        }

        /// <summary>
        /// Move the cursor one character to the right.  This may move the cursor to the next
        /// line of multi-line input.
        /// </summary>
        public static void ForwardChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._current < _singleton._buffer.Length)
            {
                _singleton._current += 1;
                _singleton.PlaceCursor();
            }
        }

        /// <summary>
        /// Move the cursor one character to the left.  This may move the cursor to the previous
        /// line of multi-line input.
        /// </summary>
        public static void BackwardChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._current > 0 && (_singleton._current - 1 < _singleton._buffer.Length))
            {
                _singleton._current -= 1;
                _singleton.PlaceCursor();
            }
        }

        private void ForwardWord(EditMode mode)
        {
            var findTokenMode = mode == EditMode.Windows
                                    ? FindTokenMode.Next
                                    : FindTokenMode.CurrentOrNext;
            var token = FindToken(_current, findTokenMode);

            Debug.Assert(token != null, "We'll always find EOF");

            switch (mode)
            {
            case EditMode.Emacs:
                _current = token.Kind == TokenKind.EndOfInput
                    ? _buffer.Length
                    : token.Extent.EndOffset;
                break;
            case EditMode.Windows:
                _current = token.Kind == TokenKind.EndOfInput
                    ? _buffer.Length
                    : token.Extent.StartOffset;
                break;
            }
            PlaceCursor();
        }

        /// <summary>
        /// Move the cursor forward to the end of the current word, or if between words,
        /// to the end of the next word.
        /// </summary>
        public static void EmacsForwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.ForwardWord(EditMode.Emacs);
        }

        /// <summary>
        /// Move the cursor forward to the start of the next word.
        /// </summary>
        public static void ForwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.ForwardWord(EditMode.Windows);
        }

        private void BackwardWord(EditMode mode)
        {
            var token = _singleton.FindToken(_singleton._current, FindTokenMode.Previous);

            _singleton._current = (token != null) ? token.Extent.StartOffset : 0;
            _singleton.PlaceCursor();
        }

        /// <summary>
        /// Move the cursor back to the start of the current word, or if between words,
        /// the start of the previous word.
        /// </summary>
        public static void BackwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.BackwardWord(EditMode.Windows);
        }

        /// <summary>
        /// Move the cursor back to the start of the current word, or if between words,
        /// the start of the previous word.
        /// </summary>
        public static void EmacsBackwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.BackwardWord(EditMode.Emacs);
        }

        /// <summary>
        /// Go to the matching brace, paren, or square bracket
        /// </summary>
        public static void GotoBrace(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._current >= _singleton._buffer.Length)
            {
                Ding();
                return;
            }

            var charAtPoint = _singleton._buffer[_singleton._current];

            Token token = null;
            var index = 0;
            for (; index < _singleton._tokens.Length; index++)
            {
                token = _singleton._tokens[index];
                if (token.Extent.StartOffset == _singleton._current)
                    break;
            }

            TokenKind toMatch;
            int direction;
            switch (token.Kind)
            {
            case TokenKind.LParen:   toMatch = TokenKind.RParen; direction = 1; break;
            case TokenKind.LCurly:   toMatch = TokenKind.RCurly; direction = 1; break;
            case TokenKind.LBracket: toMatch = TokenKind.RBracket; direction = 1; break;

            case TokenKind.RParen:   toMatch = TokenKind.LParen; direction = -1; break;
            case TokenKind.RCurly:   toMatch = TokenKind.LCurly; direction = -1; break;
            case TokenKind.RBracket: toMatch = TokenKind.LBracket; direction = -1; break;

            default:
                // Nothing to match (don't match inside strings/comments)
                Ding();
                return;
            }

            var matchCount = 0;
            var limit = (direction > 0) ? _singleton._tokens.Length - 1 : -1;
            for (; index != limit; index += direction)
            {
                var t = _singleton._tokens[index];
                if (t.Kind == token.Kind)
                {
                    matchCount++;
                }
                else if (t.Kind == toMatch)
                {
                    matchCount--;
                    if (matchCount == 0)
                    {
                        _singleton._current = t.Extent.StartOffset;
                        _singleton.PlaceCursor();
                        return;
                    }
                }
            }
            Ding();
        }

#endregion Movement

#region History

        /// <summary>
        /// Add a command to the history - typically used to restore
        /// history from a previous session.
        /// </summary>
        public static void AddToHistory(string command)
        {
            command = command.Replace("\r\n", "\n");
            _singleton.MaybeAddToHistory(command, new List<EditItem>(), 0);
        }

        /// <summary>
        /// Clears history in PSReadline.  This does not affect PowerShell history.
        /// </summary>
        public static void ClearHistory()
        {
            _singleton._history.Clear();
            _singleton._hashedHistory.Clear();
            _singleton._currentHistoryIndex = 0;
        }

        private void UpdateFromHistory(bool moveCursor)
        {
            string line;
            if (_currentHistoryIndex == _history.Count)
            {
                line = _savedCurrentLine._line;
                _edits = _savedCurrentLine._edits;
                _undoEditIndex = _savedCurrentLine._undoEditIndex;
            }
            else
            {
                line = _history[_currentHistoryIndex]._line;
                _edits = _history[_currentHistoryIndex]._edits;
                _undoEditIndex = _history[_currentHistoryIndex]._undoEditIndex;
            }
            _buffer.Clear();
            _buffer.Append(line);
            if (moveCursor)
            {
                _current = _buffer.Length;
            }
            Render();
        }

        private void SaveCurrentLine()
        {
            if (_singleton._currentHistoryIndex == _history.Count)
            {
                _savedCurrentLine._line = _buffer.ToString();
                _savedCurrentLine._edits = _edits;
                _savedCurrentLine._undoEditIndex = _undoEditIndex;
            }
        }

        /// <summary>
        /// Replace the current input with the 'previous' item from PSReadline history.
        /// </summary>
        public static void PreviousHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.SaveCurrentLine();
            if (_singleton._currentHistoryIndex > 0)
            {
                _singleton._currentHistoryIndex -= 1;
                _singleton.UpdateFromHistory(moveCursor: true);
            }
        }

        /// <summary>
        /// Replace the current input with the 'next' item from PSReadline history.
        /// </summary>
        public static void NextHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.SaveCurrentLine();
            if (_singleton._currentHistoryIndex < _singleton._history.Count)
            {
                _singleton._currentHistoryIndex += 1;
                _singleton.UpdateFromHistory(moveCursor: true);
            }
        }

        private void HistorySearch(bool backward)
        {
            if (_searchHistoryCommandCount == 0)
            {
                _searchHistoryPrefix = _buffer.ToString(0, _current);
            }
            _searchHistoryCommandCount += 1;

            int incr = backward ? -1 : +1;
            for (int i = _currentHistoryIndex + incr; i >=0 && i < _history.Count; i += incr)
            {
                if (_history[i]._line.StartsWith(_searchHistoryPrefix))
                {
                    _currentHistoryIndex = i;
                    UpdateFromHistory(moveCursor: Options.HistorySearchCursorMovesToEnd);
                    break;
                }
            }
        }

        /// <summary>
        /// Move to the first item in the history.
        /// </summary>
        public static void BeginningOfHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.SaveCurrentLine();
            _singleton._currentHistoryIndex = 0;
            _singleton.UpdateFromHistory(moveCursor: _singleton.Options.HistorySearchCursorMovesToEnd);
        }

        /// <summary>
        /// Move to the last item (the current input) in the history.
        /// </summary>
        public static void EndOfHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton._currentHistoryIndex = _singleton._history.Count;
            _singleton.UpdateFromHistory(moveCursor: _singleton.Options.HistorySearchCursorMovesToEnd);
        }

        /// <summary>
        /// Replace the current input with the 'previous' item from PSReadline history
        /// that matches the characters between the start and the input and the cursor.
        /// </summary>
        public static void HistorySearchBackward(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.SaveCurrentLine();
            _singleton.HistorySearch(backward: true);
        }

        /// <summary>
        /// Replace the current input with the 'next' item from PSReadline history
        /// that matches the characters between the start and the input and the cursor.
        /// </summary>
        public static void HistorySearchForward(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.SaveCurrentLine();
            _singleton.HistorySearch(backward: false);
        }

#endregion History

#region Completion

        /// <summary>
        /// Attempt to complete the text surrounding the cursor with the next
        /// available completion.
        /// </summary>
        public static void TabCompleteNext(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.Complete(forward: true);
        }

        /// <summary>
        /// Attempt to complete the text surrounding the cursor with the previous
        /// available completion.
        /// </summary>
        public static void TabCompletePrevious(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.Complete(forward: false);
        }

        private static bool IsSingleQuote(char c)
        {
            return c == '\'' || c == (char)8216 || c == (char)8217 || c == (char)8218;
        }

        private static bool IsDoubleQuote(char c)
        {
            return c == '"' || c == (char)8220 || c == (char)8221;
        }

        private static bool IsQuoted(string s)
        {
            if (s.Length >= 2)
            {
                var first = s[0];
                var last = s[s.Length - 1];

                return ((IsSingleQuote(first) && IsSingleQuote(last))
                        ||
                        (IsDoubleQuote(first) && IsDoubleQuote(last)));
            }
            return false;
        }

        private static string GetUnquotedText(string s, bool consistentQuoting)
        {
            if (!consistentQuoting && IsQuoted(s))
            {
                s = s.Substring(1, s.Length - 2);
            }
            return s;
        }

        /// <summary>
        /// Attempt to perform completion on the text surrounding the cursor.
        /// If there are multiple possible completions, the longest unambiguous
        /// prefix is used for completion.  If trying to complete the longest
        /// unambiguous completion, a list of possible completions is displayed.
        /// </summary>
        public static void Complete(ConsoleKeyInfo? key = null, object arg = null)
        {
            var completions = _singleton.GetCompletions();
            if (completions == null || completions.CompletionMatches.Count == 0)
                return;

            if (_singleton._tabCommandCount > 0)
            {
                if (completions.CompletionMatches.Count == 1)
                {
                    Ding();
                }
                else
                {
                    PossibleCompletions();
                }
                return;
            }

            // Find the longest unambiguous prefix.  This might be the empty
            // string, in which case we don't want to remove any of the users, instead
            // we'll immediately show possible completions.
            // For the purposes of unambiguous prefix, we'll ignore quotes if
            // some completions aren't quoted.
            var firstResult = completions.CompletionMatches[0];
            int quotedCompletions = completions.CompletionMatches.Count(match => IsQuoted(match.CompletionText));
            bool consistentQuoting =
                quotedCompletions == 0 ||
                (quotedCompletions == completions.CompletionMatches.Count &&
                 quotedCompletions == completions.CompletionMatches.Count(
                    m => m.CompletionText[0] == firstResult.CompletionText[0]));

            bool ambiguous = false;
            var replacementText = GetUnquotedText(firstResult.CompletionText, consistentQuoting);
            foreach (var match in completions.CompletionMatches.Skip(1)) 
            {
                var matchText = GetUnquotedText(match.CompletionText, consistentQuoting);
                for (int i = 0; i < replacementText.Length; i++)
                {
                    if (i == matchText.Length
                        || char.ToLowerInvariant(replacementText[i]) != char.ToLowerInvariant(matchText[i]))
                    {
                        ambiguous = true;
                        replacementText = replacementText.Substring(0, i);
                        break;
                    }
                }
                if (replacementText.Length == 0)
                {
                    break;
                }
            }

            if (replacementText.Length > 0)
            {
                Replace(completions.ReplacementIndex, completions.ReplacementLength, replacementText);
                completions.ReplacementLength = replacementText.Length;

                if (ambiguous)
                {
                    Ding();
                }
            }
            else
            {
                // No common prefix, don't wait for a second tab, just show the possible completions
                // right away.
                PossibleCompletions();
            }

            _singleton._tabCommandCount += 1;
        }

        private CommandCompletion GetCompletions()
        {
            if (_tabCommandCount == 0)
            {
                try
                {
                    _tabCompletions = null;

                    // Could use the overload that takes an AST as it's faster (we've already parsed the
                    // input for coloring) but that overload is a little more complicated in passing in the
                    // cursor position.
                    var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
                    _tabCompletions = CommandCompletion.CompleteInput(_buffer.ToString(), _current, null, ps);

                    if (_tabCompletions.CompletionMatches.Count == 0)
                        return null;
                }
                catch (Exception)
                {
                }                
            }

            return _tabCompletions;
        }

        private void Complete(bool forward)
        {
            var completions = GetCompletions();
            if (completions == null)
                return;

            completions.CurrentMatchIndex += forward ? 1 : -1;
            if (completions.CurrentMatchIndex < 0)
            {
                completions.CurrentMatchIndex = completions.CompletionMatches.Count - 1;
            }
            else if (completions.CurrentMatchIndex == completions.CompletionMatches.Count)
            {
                completions.CurrentMatchIndex = 0;
            }

            var replacementText = completions.CompletionMatches[completions.CurrentMatchIndex].CompletionText;
            Replace(completions.ReplacementIndex, completions.ReplacementLength, replacementText);
            completions.ReplacementLength = replacementText.Length;
            _tabCommandCount += 1;
        }

        /// <summary>
        /// Display the list of possible completions.
        /// </summary>
        public static void PossibleCompletions(ConsoleKeyInfo? key = null, object arg = null)
        {
            var completions = _singleton.GetCompletions();
            if (completions == null || completions.CompletionMatches.Count == 0)
            {
                Ding();
                return;
            }

            // Don't overwrite any of the line - so move to first line after the end of our buffer.
            var coords = _singleton.ConvertOffsetToCoordinates(_singleton._buffer.Length);
            _singleton.PlaceCursor(0, coords.Y + 1);

            if (completions.CompletionMatches.Count >= _singleton._options.CompletionQueryItems)
            {
                if (!PromptYesOrNo(string.Format(PSReadLineResources.DisplayAllPossibilities, completions.CompletionMatches.Count)))
                {
                    _singleton.PlaceCursor(0, coords.Y + 1);
                    WriteLine("");
                    _singleton.PlaceCursor(coords.X, coords.Y);
                    return;
                }
            }

            var sb = new StringBuilder();
            var matches = completions.CompletionMatches.Select(
                completion =>
                new {ListItemText = Regex.Replace(completion.ListItemText, "\n.*", "..."),
                     ToolTip = Regex.Replace(completion.ToolTip, "\n.*", "...")})
                               .ToArray();
            var minColWidth = matches.Max(c => c.ListItemText.Length);
            minColWidth += 2;

            if (_singleton.Options.ShowToolTips)
            {
                const string seperator = "- ";
                var maxTooltipWidth = Console.BufferWidth - minColWidth - seperator.Length;

                foreach (var match in matches)
                {
                    sb.Append(match.ListItemText);
                    var spacesNeeded = minColWidth - match.ListItemText.Length;
                    if (spacesNeeded > 0)
                        sb.Append(' ', spacesNeeded);
                    sb.Append(seperator);
                    var toolTip = match.ToolTip.Length <= maxTooltipWidth
                                      ? match.ToolTip
                                      : match.ToolTip.Substring(0, maxTooltipWidth);
                    sb.Append(toolTip.Trim());
                    WriteLine(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                // Use BufferWidth here instead of WindowWidth?  Probably shouldn't.
                var screenColumns = Console.WindowWidth;
                var displayColumns = Math.Max(1, screenColumns / minColWidth);
                var displayRows = (completions.CompletionMatches.Count + displayColumns - 1) / displayColumns;
                for (var row = 0; row < displayRows; row++)
                {
                    for (var col = 0; col < displayColumns; col++)
                    {
                        var index = row + (displayRows * col);
                        if (index >= matches.Length)
                            break;
                        var item = matches[index].ListItemText;
                        sb.Append(item);
                        sb.Append(' ', minColWidth - item.Length);
                    }
                    WriteLine(sb.ToString());
                    sb.Clear();
                }
            }

            _singleton._initialY = Console.CursorTop;
            _singleton.Render();
        }

#endregion Completion

        #region Kill/Yank

        /// <summary>
        /// Mark the current loction of the cursor for use in a subsequent editing command.
        /// </summary>
        public static void SetMark(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton._mark = _singleton._current;
        }

        /// <summary>
        /// The cursor is placed at the location of the mark and the mark is moved
        /// to the location of the cursor.
        /// </summary>
        public static void ExchangePointAndMark(ConsoleKeyInfo? key = null, object arg = null)
        {
            var tmp = _singleton._mark;
            _singleton._mark = _singleton._current;
            _singleton._current = tmp;
            _singleton.PlaceCursor();
        }

        /// <summary>
        /// The contents of the kill ring are cleared.
        /// </summary>
        public static void ClearKillRing()
        {
            _singleton._killRing.Clear();
            _singleton._killIndex = -1;    // So first add indexes 0.
        }

        private void Kill(int start, int length)
        {
            if (length > 0)
            {
                var killText = _buffer.ToString(start, length);
                SaveEditItem(EditItemDelete.Create(killText, start));
                _buffer.Remove(start, length);
                _current = start;
                Render();
                if (_killCommandCount > 0)
                {
                    _killRing[_killIndex] += killText;
                }
                else
                {
                    if (_killRing.Count < Options.MaximumKillRingCount)
                    {
                        _killRing.Add(killText);
                        _killIndex = _killRing.Count - 1;
                    }
                    else
                    {
                        _killIndex += 1;
                        if (_killIndex == _killRing.Count)
                        {
                            _killIndex = 0;
                        }
                        _killRing[_killIndex] = killText;
                    }
                }
            }
            _killCommandCount += 1;
        }

        /// <summary>
        /// Clear the input from the cursor to the end of the input.  The cleared text is placed
        /// in the kill ring.
        /// </summary>
        public static void KillLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.Kill(_singleton._current, _singleton._buffer.Length - _singleton._current);
        }

        /// <summary>
        /// Clear the input from the start of the input to the cursor.  The cleared text is placed
        /// in the kill ring.
        /// </summary>
        public static void BackwardKillLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.Kill(0, _singleton._current);
        }

        /// <summary>
        /// Clear the input from the cursor to the end of the current word.  If the cursor
        /// is between words, the input is cleared from the cursor to the end of the next word.
        /// The cleared text is placed in the kill ring.
        /// </summary>
        public static void KillWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            var token = _singleton.FindToken(_singleton._current, FindTokenMode.CurrentOrNext);
            var end = (token.Kind == TokenKind.EndOfInput)
                ? _singleton._buffer.Length 
                : token.Extent.EndOffset;
            _singleton.Kill(_singleton._current, end - _singleton._current);
        }

        /// <summary>
        /// Clear the input from the start of the current word to the cursor.  If the cursor
        /// is between words, the input is cleared from the start of the previous word to the
        /// cursor.  The cleared text is placed in the kill ring.
        /// </summary>
        public static void KillBackwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            var token = _singleton.FindToken(_singleton._current, FindTokenMode.Previous);
            var start = token == null 
                ? 0
                : token.Extent.StartOffset;
            _singleton.Kill(start, _singleton._current - start);
        }

        private void YankImpl()
        {
            if (_killRing.Count == 0)
                return;

            // Starting a yank session, yank the last thing killed and
            // remember where we started.
            _mark = _yankStartPoint = _current;
            Insert(_killRing[_killIndex]);
            
            _yankCommandCount += 1;
        }

        /// <summary>
        /// Add the most recently killed text to the input.
        /// </summary>
        public static void Yank(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.YankImpl();
        }

        private void YankPopImpl()
        {
            if (_yankCommandCount == 0)
                return;

            _killIndex -= 1;
            if (_killIndex < 0)
            {
                _killIndex = _killRing.Count - 1;
            }
            var yankText = _killRing[_killIndex];
            Replace(_yankStartPoint, _current - _yankStartPoint, yankText);
            _yankCommandCount += 1;
        }

        /// <summary>
        /// If the previous operation was Yank or YankPop, replace the previously yanked
        /// text with the next killed text from the kill ring.
        /// </summary>
        public static void YankPop(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.YankPopImpl();
        }

#endregion Kill/Yank

        private enum FindTokenMode
        {
            CurrentOrNext,
            Next,
            Previous,
        }

        static bool OffsetWithinToken(int offset, Token token)
        {
           return offset < token.Extent.EndOffset && offset >= token.Extent.StartOffset;
        }

        private Token FindNestedToken(int offset, IList<Token> tokens, FindTokenMode mode)
        {
            Token token = null;
            bool foundNestedToken = false;
            int i;
            for (i = tokens.Count - 1; i >= 0; i--)
            {
                if (OffsetWithinToken(offset, tokens[i]))
                {
                    token = tokens[i];
                    var strToken = token as StringExpandableToken;
                    if (strToken != null && strToken.NestedTokens != null)
                    {
                        var nestedToken = FindNestedToken(offset, strToken.NestedTokens, mode);
                        if (nestedToken != null)
                        {
                            token = nestedToken;
                            foundNestedToken = true;
                        }
                    }
                    break;
                }
                if (offset >= tokens[i].Extent.EndOffset)
                {
                    break;
                }
            }

            switch (mode)
            {
            case FindTokenMode.CurrentOrNext:
                if (token == null && (i + 1) < tokens.Count)
                {
                    token = tokens[i + 1];
                }
                break;
            case FindTokenMode.Next:
                if (!foundNestedToken)
                {
                    // If there is no next token, return null (happens with nested
                    // tokens where there is no EOF/EOS token).
                    token = ((i + 1) < tokens.Count) ? tokens[i + 1] : null;
                }
                break;
            case FindTokenMode.Previous:
                if (token == null)
                {
                    if (i >= 0)
                    {
                        token = tokens[i];
                    }
                }
                else if (offset == token.Extent.StartOffset)
                {
                    token = i > 0 ? tokens[i - 1] : null;
                }
                break;
            }

            return token;
        }

        private Token FindToken(int current, FindTokenMode mode)
        {
            if (_tokens == null)
            {
                ParseInput();
            }

            return FindNestedToken(current, _tokens, mode);
        }

        private void MoveCursor(int offset)
        {
            _current = offset;
            PlaceCursor();
        }

        /// <summary>
        /// Insert a character at the current position.  Supports undo.
        /// </summary>
        /// <param name="c">Character to insert</param>
        public static void Insert(char c)
        {
            _singleton.SaveEditItem(EditItemInsertChar.Create(c, _singleton._current));

            // Use Append if possible because Insert at end makes StringBuilder quite slow.
            if (_singleton._current == _singleton._buffer.Length)
            {
                _singleton._buffer.Append(c);
            }
            else
            {
                _singleton._buffer.Insert(_singleton._current, c);
            }
            _singleton._current += 1;
            _singleton.Render();
        }

        /// <summary>
        /// Insert a string at the current position.  Supports undo.
        /// </summary>
        /// <param name="s">String to insert</param>
        public static void Insert(string s)
        {
            _singleton.SaveEditItem(EditItemInsertString.Create(s, _singleton._current));

            // Use Append if possible because Insert at end makes StringBuilder quite slow.
            if (_singleton._current == _singleton._buffer.Length)
            {
                _singleton._buffer.Append(s);
            }
            else
            {
                _singleton._buffer.Insert(_singleton._current, s);
            }
            _singleton._current += s.Length;
            _singleton.Render();
        }

        /// <summary>
        /// Replace some text at the given position.  Supports undo.
        /// </summary>
        /// <param name="start">The start position to replace</param>
        /// <param name="length">The length to replace</param>
        /// <param name="replacement">The replacement text</param>
        public static void Replace(int start, int length, string replacement)
        {
            if (start < 0 || start > _singleton._buffer.Length)
            {
                throw new ArgumentException(PSReadLineResources.StartOutOfRange, "start");
            }
            if (length > (_singleton._buffer.Length - start))
            {
                throw new ArgumentException(PSReadLineResources.ReplacementLengthTooBig, "length");
            }

            _singleton.StartEditGroup();
            var str = _singleton._buffer.ToString(start, length);
            _singleton.SaveEditItem(EditItemDelete.Create(str, start));
            _singleton._buffer.Remove(start, length);
            if (replacement != null)
            {
                _singleton.SaveEditItem(EditItemInsertString.Create(replacement, start));
                _singleton._buffer.Insert(start, replacement);
                _singleton._current = start + replacement.Length;
            }
            _singleton.EndEditGroup();
            _singleton.Render();
        }

#region Undo
        private void SaveEditItem(EditItem editItem)
        {
            // If there is some sort of edit after an undo, forget
            // the
            int removeCount = _edits.Count - _undoEditIndex;
            if (removeCount > 0)
            {
                _edits.RemoveRange(_undoEditIndex, removeCount);
            }
            _edits.Add(editItem);
            _undoEditIndex = _edits.Count;
            _editGroupCount++;
        }

        private void StartEditGroup()
        {
            _pushedEditGroupCount.Push(_editGroupCount);
            _editGroupCount = 0;
        }

        private void EndEditGroup()
        {
            // The last _editGroupCount edits are treated as a single item for undo
            var start = _edits.Count - _editGroupCount;
            var groupedEditItems = _edits.GetRange(start, _editGroupCount);
            _edits.RemoveRange(start, _editGroupCount);
            SaveEditItem(GroupedEdit.Create(groupedEditItems));
            _editGroupCount = _pushedEditGroupCount.Pop();
        }

        /// <summary>
        /// Undo a previous edit.
        /// </summary>
        public static void Undo(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._undoEditIndex > 0)
            {
                _singleton._edits[_singleton._undoEditIndex - 1].Undo(_singleton);
                _singleton._undoEditIndex--;
                _singleton.Render();
            }
            else
            {
                Ding();
            }
        }

        /// <summary>
        /// Undo an undo.
        /// </summary>
        public static void Redo(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._undoEditIndex < _singleton._edits.Count)
            {
                _singleton._edits[_singleton._undoEditIndex].Redo(_singleton);
                _singleton._undoEditIndex++;
                _singleton.Render();
            }
            else
            {
                Ding();
            }
        }

        abstract class EditItem
        {
            public abstract void Undo(PSConsoleReadLine singleton);
            public abstract void Redo(PSConsoleReadLine singleton);
        }

        [DebuggerDisplay("Insert '{_insertedCharacter}' ({_insertStartPosition})")]
        class EditItemInsertChar : EditItem
        {
            // The character inserted is not needed for undo, only for redo
            private char _insertedCharacter;
            private int _insertStartPosition;

            public static EditItem Create(char character, int position)
            {
                return new EditItemInsertChar
                {
                    _insertedCharacter = character,
                    _insertStartPosition = position
                };
            }

            public override void Undo(PSConsoleReadLine singleton)
            {
                Debug.Assert(singleton._buffer[_insertStartPosition] == _insertedCharacter, "Character to undo is not what it should be");
                _singleton._buffer.Remove(_insertStartPosition, 1);
                _singleton._current = _insertStartPosition;
            }

            public override void Redo(PSConsoleReadLine singleton)
            {
                _singleton._buffer.Insert(_insertStartPosition, _insertedCharacter);
                _singleton._current++;
            }
        }

        [DebuggerDisplay("Insert '{_insertedString}' ({_insertStartPosition})")]
        class EditItemInsertString : EditItem
        {
            // The string inserted tells us the length to delete on undo.
            // The contents of the string are only needed for redo.
            private string _insertedString;
            private int _insertStartPosition;

            public static EditItem Create(string str, int position)
            {
                return new EditItemInsertString
                {
                    _insertedString = str,
                    _insertStartPosition = position
                };
            }

            public override void Undo(PSConsoleReadLine singleton)
            {
                Debug.Assert(singleton._buffer.ToString(_insertStartPosition, _insertedString.Length).Equals(_insertedString),
                    "Character to undo is not what it should be");
                _singleton._buffer.Remove(_insertStartPosition, _insertedString.Length);
                _singleton._current = _insertStartPosition;
            }

            public override void Redo(PSConsoleReadLine singleton)
            {
                _singleton._buffer.Insert(_insertStartPosition, _insertedString);
                _singleton._current += _insertedString.Length;
            }
        }

        [DebuggerDisplay("Delete '{_deletedString}' ({_deleteStartPosition})")]
        class EditItemDelete : EditItem
        {
            private string _deletedString;
            private int _deleteStartPosition;

            public static EditItem Create(string str, int position)
            {
                return new EditItemDelete
                {
                    _deletedString = str,
                    _deleteStartPosition = position
                };
            }

            public override void Undo(PSConsoleReadLine singleton)
            {
                _singleton._buffer.Insert(_deleteStartPosition, _deletedString);
                _singleton._current = _deleteStartPosition + _deletedString.Length;
            }

            public override void Redo(PSConsoleReadLine singleton)
            {
                _singleton._buffer.Remove(_deleteStartPosition, _deletedString.Length);
                _singleton._current = _deleteStartPosition;
            }
        }

        class GroupedEdit : EditItem
        {
            private List<EditItem> _groupedEditItems;

            public static EditItem Create(List<EditItem> groupedEditItems)
            {
                return new GroupedEdit {_groupedEditItems = groupedEditItems};
            }

            public override void Undo(PSConsoleReadLine singleton)
            {
                for (int i = _groupedEditItems.Count - 1; i >= 0; i--)
                {
                    _groupedEditItems[i].Undo(singleton);
                }
            }

            public override void Redo(PSConsoleReadLine singleton)
            {
                foreach (var editItem in _groupedEditItems)
                {
                    editItem.Redo(singleton);
                }
            }
        }

#endregion Undo

#region Rendering

        private class SavedTokenState
        {
            internal Token[] Tokens { get; set; }
            internal int Index { get; set; }
            internal ConsoleColor BackgroundColor { get; set; }
            internal ConsoleColor ForegroundColor { get; set; }
        }

        private string ParseInput()
        {
            var text = _buffer.ToString();
            _ast = Parser.ParseInput(text, out _tokens, out _parseErrors);
            return text;
        }

        private void Render()
        {
            // If there are a bunch of keys queued up, skip rendering if we've rendered
            // recently.
            if (_queuedKeys.Count > 10 && (DateTime.Now - _lastRenderTime).Milliseconds < 50)
            {
                return;
            }

            ReallyRender();
        }

        private void ReallyRender()
        {
            _renderForDemoNeeded = false;

            var text = ParseInput();

            int bufferLineCount = ConvertOffsetToCoordinates(text.Length).Y - _initialY + 1 + _demoWindowLineCount;
            int bufferWidth = Console.BufferWidth;
            if (_consoleBuffer.Length != bufferLineCount * bufferWidth)
            {
                var newBuffer = new CHAR_INFO[bufferLineCount * bufferWidth];
                Array.Copy(_consoleBuffer, newBuffer, _initialX + (Options.ExtraPromptLineCount * _bufferWidth));
                if (_consoleBuffer.Length > bufferLineCount * bufferWidth)
                {
                    // Need to erase the extra lines that we won't draw again
                    for (int i = bufferLineCount * bufferWidth; i < _consoleBuffer.Length; i++)
                    {
                        _consoleBuffer[i] = _space;
                    }
                    WriteBufferLines(_consoleBuffer, ref _initialY);
                }
                _consoleBuffer = newBuffer;
            }

            var tokenStack = new Stack<SavedTokenState>();
            tokenStack.Push(new SavedTokenState
            {
                Tokens          = _tokens,
                Index           = 0,
                BackgroundColor = _initialBackgroundColor,
                ForegroundColor = _initialForegroundColor
            });

            int j               = _initialX + (_bufferWidth * Options.ExtraPromptLineCount);
            var backgroundColor = _initialBackgroundColor;
            var foregroundColor = _initialForegroundColor;

            for (int i = 0; i < text.Length; i++)
            {
                // Figure out the color of the character - if it's in a token,
                // use the tokens color otherwise use the initial color.
                var state = tokenStack.Peek();
                var token = state.Tokens[state.Index];
                if (i == token.Extent.EndOffset)
                {
                    if (token == state.Tokens[state.Tokens.Length - 1])
                    {
                        tokenStack.Pop();
                        state = tokenStack.Peek();
                    }
                    foregroundColor = state.ForegroundColor;
                    backgroundColor = state.BackgroundColor;

                    token = state.Tokens[++state.Index];
                }

                if (i == token.Extent.StartOffset)
                {
                    GetTokenColors(token, ref foregroundColor, ref backgroundColor);

                    var stringToken = token as StringExpandableToken;
                    if (stringToken != null)
                    {
                        // We might have nested tokens.
                        if (stringToken.NestedTokens != null && stringToken.NestedTokens.Any())
                        {
                            var tokens = new Token[stringToken.NestedTokens.Count + 1];
                            stringToken.NestedTokens.CopyTo(tokens, 0);
                            // NestedTokens doesn't have an "EOS" token, so we use
                            // the string literal token for that purpose.
                            tokens[tokens.Length - 1] = stringToken;

                            tokenStack.Push(new SavedTokenState
                            {
                                Tokens          = tokens,
                                Index           = 0,
                                BackgroundColor = backgroundColor,
                                ForegroundColor = foregroundColor
                            });
                        }
                    }
                }

                if (text[i] == '\n')
                {
                    while ((j % bufferWidth) != 0)
                    {
                        _consoleBuffer[j++] = _space;
                    }

                    for (int k = 0; k < Options.ContinuationPrompt.Length; k++, j++)
                    {
                        _consoleBuffer[j].UnicodeChar = Options.ContinuationPrompt[k];
                        _consoleBuffer[j].ForegroundColor = Options.ContinuationPromptForegroundColor;
                        _consoleBuffer[j].BackgroundColor = Options.ContinuationPromptBackgroundColor;
                    }
                }
                else if (char.IsControl(text[i]))
                {
                    _consoleBuffer[j].UnicodeChar = '^';
                    _consoleBuffer[j].ForegroundColor = foregroundColor;
                    _consoleBuffer[j++].BackgroundColor = backgroundColor;
                    _consoleBuffer[j].UnicodeChar = (char)('@' + text[i]);
                    _consoleBuffer[j].ForegroundColor = foregroundColor;
                    _consoleBuffer[j++].BackgroundColor = backgroundColor;
                }
                else
                {
                    _consoleBuffer[j].UnicodeChar = text[i];
                    _consoleBuffer[j].ForegroundColor = foregroundColor;
                    _consoleBuffer[j++].BackgroundColor = backgroundColor;
                }
            }
            for (; j < (_consoleBuffer.Length - (_demoWindowLineCount * _bufferWidth)); j++)
            {
                _consoleBuffer[j] = _space;
            }

            if (_demoMode)
            {
                RenderDemoWindow(j);
            }

            bool rendered = false;
            if (_parseErrors.Length > 0)
            {
                int promptChar = _initialX - 1 + (_bufferWidth * Options.ExtraPromptLineCount);

                while (promptChar >= 0)
                {
                    if (char.IsSymbol((char)_consoleBuffer[promptChar].UnicodeChar))
                    {
                        ConsoleColor prevColor = _consoleBuffer[promptChar].ForegroundColor;
                        _consoleBuffer[promptChar].ForegroundColor = ConsoleColor.Red;
                        WriteBufferLines(_consoleBuffer, ref _initialY);
                        rendered = true;
                        _consoleBuffer[promptChar].ForegroundColor = prevColor;
                        break;
                    }
                    promptChar -= 1;
                }
            }

            if (!rendered)
            {
                WriteBufferLines(_consoleBuffer, ref _initialY);
            }

            PlaceCursor();

            if (_demoMode && (_initialY + bufferLineCount + 1) > (Console.WindowTop + Console.WindowHeight))
            {
                Console.WindowTop = _initialY + bufferLineCount + 1 - Console.WindowHeight;
            }

            _lastRenderTime = DateTime.Now;
        }

        private static void WriteBufferLines(CHAR_INFO[] buffer, ref int top)
        {
            var handle = NativeMethods.GetStdHandle((uint) StandardHandleId.Output);

            int bufferWidth = Console.BufferWidth;
            int bufferLineCount = buffer.Length / bufferWidth;
            if ((top + bufferLineCount) > Console.BufferHeight)
            {
                var scrollCount = (top + bufferLineCount) - Console.BufferHeight;
                ScrollBuffer(scrollCount);
                top -= scrollCount;
            }
            var bufferSize = new COORD
            {
                X = (short) bufferWidth,
                Y = (short) bufferLineCount
            };
            var bufferCoord = new COORD {X = 0, Y = 0};
            var writeRegion = new SMALL_RECT
            {
                Top = (short) top,
                Left = 0,
                Bottom = (short) (top + bufferLineCount - 1),
                Right = (short) bufferWidth
            };
            NativeMethods.WriteConsoleOutput(handle, buffer,
                                             bufferSize, bufferCoord, ref writeRegion);
        }

        private static CHAR_INFO[] ReadBufferLines(int top, int count)
        {
            var result = new CHAR_INFO[Console.BufferWidth * count];
            var handle = NativeMethods.GetStdHandle((uint) StandardHandleId.Output);

            var readBufferSize = new COORD {
                X = (short)Console.BufferWidth,
                Y = (short)count};
            var readBufferCoord = new COORD {X = 0, Y = 0};
            var readRegion = new SMALL_RECT
            {
                Top = (short)top,
                Left = 0,
                Bottom = (short)(top + count),
                Right = (short)(Console.BufferWidth - 1)
            };
            NativeMethods.ReadConsoleOutput(handle, result,
                readBufferSize, readBufferCoord, ref readRegion);
            return result;
        }

        private void GetTokenColors(Token token, ref ConsoleColor foregroundColor, ref ConsoleColor backgroundColor)
        {
            switch (token.Kind)
            {
            case TokenKind.Comment:
                foregroundColor = _options.CommentForegroundColor;
                backgroundColor = _options.CommentBackgroundColor;
                return;

            case TokenKind.Parameter:
                foregroundColor = _options.ParameterForegroundColor;
                backgroundColor = _options.ParameterBackgroundColor;
                return;

            case TokenKind.Variable:
            case TokenKind.SplattedVariable:
                foregroundColor = _options.VariableForegroundColor;
                backgroundColor = _options.VariableBackgroundColor;
                return;

            case TokenKind.StringExpandable:
            case TokenKind.StringLiteral:
            case TokenKind.HereStringExpandable:
            case TokenKind.HereStringLiteral:
                foregroundColor = _options.StringForegroundColor;
                backgroundColor = _options.StringBackgroundColor;
                return;

            case TokenKind.Number:
                foregroundColor = _options.NumberForegroundColor;
                backgroundColor = _options.NumberBackgroundColor;
                return;
            }

            if ((token.TokenFlags & TokenFlags.CommandName) != 0)
            {
                foregroundColor = _options.CommandForegroundColor;
                backgroundColor = _options.CommandBackgroundColor;
                return;
            }

            if ((token.TokenFlags & TokenFlags.Keyword) != 0)
            {
                foregroundColor = _options.KeywordForegroundColor;
                backgroundColor = _options.KeywordBackgroundColor;
                return;
            }

            if ((token.TokenFlags & (TokenFlags.BinaryOperator | TokenFlags.UnaryOperator | TokenFlags.AssignmentOperator)) != 0)
            {
                foregroundColor = _options.OperatorForegroundColor;
                backgroundColor = _options.OperatorBackgroundColor;
                return;
            }

            if ((token.TokenFlags & TokenFlags.TypeName) != 0)
            {
                foregroundColor = _options.TypeForegroundColor;
                backgroundColor = _options.TypeBackgroundColor;
                return;
            }

            if ((token.TokenFlags & TokenFlags.MemberName) != 0)
            {
                foregroundColor = _options.MemberForegroundColor;
                backgroundColor = _options.MemberBackgroundColor;
                return;
            }

            foregroundColor = _options.DefaultTokenForegroundColor;
            backgroundColor = _options.DefaultTokenBackgroundColor;
        }

        private static void ScrollBuffer(int lines)
        {
            var handle = NativeMethods.GetStdHandle((uint) StandardHandleId.Output);

            var scrollRectangle = new SMALL_RECT
            {
                Top = (short) lines,
                Left = 0,
                Bottom = (short) (lines + Console.BufferHeight - 1),
                Right = (short)Console.BufferWidth
            };
            var destinationOrigin = new COORD {X = 0, Y = 0};
            var fillChar = new CHAR_INFO(' ', Console.ForegroundColor, Console.BackgroundColor);
            NativeMethods.ScrollConsoleScreenBuffer(handle, ref scrollRectangle, ref scrollRectangle, destinationOrigin, ref fillChar);
        }

        private void PlaceCursor(int x, int y)
        {
            if ((y + _demoWindowLineCount) >= Console.BufferHeight)
            {
                ScrollBuffer((y + _demoWindowLineCount) - Console.BufferHeight + 1);
                y = Console.BufferHeight - 1;
            }
            Console.SetCursorPosition(x, y);
        }

        private void PlaceCursor()
        {
            var coordinates = ConvertOffsetToCoordinates(_current);
            PlaceCursor(coordinates.X, coordinates.Y);
        }

        private COORD ConvertOffsetToCoordinates(int offset)
        {
            int x = _initialX;
            int y = _initialY + Options.ExtraPromptLineCount;

            int bufferWidth = Console.BufferWidth;
            var continuationPromptLength = Options.ContinuationPrompt.Length;
            for (int i = 0; i < offset; i++)
            {
                char c = _buffer[i];
                if (c == '\n')
                {
                    y += 1;
                    x = continuationPromptLength;
                }
                else
                {
                    x += char.IsControl(c) ? 2 : 1;
                    // Wrap?  No prompt when wrapping
                    if (x >= bufferWidth)
                    {
                        x -= bufferWidth;
                        y += 1;
                    }
                }
            }

            return new COORD {X = (short)x, Y = (short)y};
        }

        /// <summary>
        /// Notify the user based on their preference for notification.
        /// </summary>
        public static void Ding()
        {
            switch (_singleton.Options.BellStyle)
            {
            case BellStyle.None:
                break;
            case BellStyle.Audible:
                Console.Beep(_singleton.Options.DingTone, _singleton.Options.DingDuration);
                break;
            case BellStyle.Visual:
                // TODO: flash prompt? command line?
                break;
            }
        }

        // Console.WriteLine works as expected in PowerShell but not in the unit test framework
        // so we use our own special (and limited) version as we don't need WriteLine much.
        // The unit test framework redirects stdout - so it would see Console.WriteLine calls.
        // Unfortunately, we are testing exact placement of characters on the screen, so redirection
        // doesn't work for us.
        static private void WriteImpl(string s)
        {
            Debug.Assert(s.Length <= Console.BufferWidth);

            var handle = NativeMethods.GetStdHandle((uint) StandardHandleId.Output);

            var buffer = new CHAR_INFO[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                Debug.Assert(s[i] != '\n');
                buffer[i] = new CHAR_INFO(s[i], Console.ForegroundColor, Console.BackgroundColor);
            }

            var bufferSize = new COORD
                             {
                                 X = (short) s.Length,
                                 Y = 1
                             };
            var bufferCoord = new COORD {X = 0, Y = 0};
            var writeRegion = new SMALL_RECT
                              {
                                  Top = (short) Console.CursorTop,
                                  Left = 0,
                                  Bottom = (short) Console.CursorTop,
                                  Right = (short) s.Length
                              };
            NativeMethods.WriteConsoleOutput(handle, buffer, bufferSize, bufferCoord, ref writeRegion);
        }

        static private void WriteLine(string s)
        {
            Debug.Assert(s.Length <= Console.BufferWidth);

            var spaces = Console.BufferWidth - s.Length;
            if (spaces > 0)
            {
                s = s + new string(' ', spaces);
            }
            WriteImpl(s);

            _singleton.PlaceCursor(0, Console.CursorTop + 1);
        }

        static private void Write(string s)
        {
            WriteImpl(s);

            _singleton.PlaceCursor(s.Length, Console.CursorTop);
        }

        static private bool PromptYesOrNo(string s)
        {
            Write(s);

            var key = ReadKey();
            return key.Key == ConsoleKey.Y;
        }

        private void RenderDemoWindow(int windowStart)
        {
            int i;

            Action<int, char> setChar = (index, c) =>
            {
                _consoleBuffer[index].UnicodeChar = c;
                _consoleBuffer[index].ForegroundColor = ConsoleColor.DarkCyan;
                _consoleBuffer[index].BackgroundColor = ConsoleColor.White;
            };

            for (i = 0; i < _bufferWidth; i++)
            {
                _consoleBuffer[windowStart + i].UnicodeChar = ' ';
                _consoleBuffer[windowStart + i].ForegroundColor = _initialForegroundColor;
                _consoleBuffer[windowStart + i].BackgroundColor = _initialBackgroundColor;
            }
            windowStart += _bufferWidth;

            const int extraSpace = 2;
            // Draw the box
            setChar(windowStart + extraSpace, (char)9484); // upper left
            setChar(windowStart + _bufferWidth * 2 + extraSpace, (char)9492); // lower left
            setChar(windowStart + _bufferWidth - 1 - extraSpace, (char)9488); // upper right
            setChar(windowStart + _bufferWidth * 3 - 1 - extraSpace, (char)9496); // lower right
            setChar(windowStart + _bufferWidth + extraSpace, (char)9474); // side
            setChar(windowStart + _bufferWidth * 2 - 1 - extraSpace, (char)9474); // side

            for (i = 1 + extraSpace; i < _bufferWidth - 1 - extraSpace; i++)
            {
                setChar(windowStart + i, (char)9472);
                setChar(windowStart + i + 2 * _bufferWidth, (char)9472);
            }

            while (_savedKeys.Count > 0)
            {
                var key = _savedKeys.Dequeue();
                _demoStrings.Enqueue(key.ToGestureString());
            }

            int charsToDisplay = _bufferWidth - 2 - (2 * extraSpace);
            i = windowStart + _bufferWidth + 1 + extraSpace;
            bool first = true;
            for (int j = _demoStrings.Count; j > 0; j--)
            {
                string eventString = _demoStrings[j - 1];
                if ((eventString.Length + (first ? 0 : 1)) > charsToDisplay)
                    break;

                if (!first)
                {
                    setChar(i++, ' ');
                    charsToDisplay--;
                }

                foreach (char c in eventString)
                {
                    setChar(i, c);
                    if (first)
                    {
                        // Invert the first word to highlight it
                        var color = _consoleBuffer[i].ForegroundColor;
                        _consoleBuffer[i].ForegroundColor = _consoleBuffer[i].BackgroundColor;
                        _consoleBuffer[i].BackgroundColor = color;
                    }
                    i++;
                    charsToDisplay--;
                }

                first = false;
            }
            while (charsToDisplay-- > 0)
            {
                setChar(i++, ' ');
            }
        }

        private void ClearDemoWindow()
        {
            int bufferWidth = Console.BufferWidth;
            var charInfoBuffer = new CHAR_INFO[bufferWidth * 3];

            for (int i = 0; i < charInfoBuffer.Length; i++)
            {
                charInfoBuffer[i].UnicodeChar = ' ';
                charInfoBuffer[i].ForegroundColor = _initialForegroundColor;
                charInfoBuffer[i].BackgroundColor= _initialBackgroundColor;
            }

            int bufferLineCount = ConvertOffsetToCoordinates(_buffer.Length).Y - _initialY + 1;
            int y = _initialY + bufferLineCount + 1;
            WriteBufferLines(charInfoBuffer, ref y);
        }

#endregion Rendering

        /// <summary>
        /// Turn on demo mode (display events like keys pressed)
        /// </summary>
        public static void EnableDemoMode(ConsoleKeyInfo? key = null, object arg = null)
        {
            const int windowLineCount = 4;  // 1 blank line, 2 border lines, 1 line of info
            _singleton._captureKeys = true;
            _singleton._demoMode = true;
            _singleton._demoWindowLineCount = windowLineCount;
            var newBuffer = new CHAR_INFO[_singleton._consoleBuffer.Length + (windowLineCount * _singleton._bufferWidth)];
            Array.Copy(_singleton._consoleBuffer, newBuffer,
                _singleton._initialX + (_singleton.Options.ExtraPromptLineCount * _singleton._bufferWidth));
            _singleton._consoleBuffer = newBuffer;
            _singleton.Render();
        }

        /// <summary>
        /// Turn off demo mode (display events like keys pressed)
        /// </summary>
        public static void DisableDemoMode(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton._savedKeys.Clear();
            _singleton._captureKeys = false;
            _singleton._demoMode = false;
            _singleton._demoStrings.Clear();
            _singleton._demoWindowLineCount = 0;
            _singleton.ClearDemoWindow();
        }

        private void SetOptionsInternal(SetPSReadlineOption options)
        {
            if (options.ContinuationPrompt != null)
            {
                Options.ContinuationPrompt = options.ContinuationPrompt;
            }
            if (options._continuationPromptForegroundColor.HasValue)
            {
                Options.ContinuationPromptForegroundColor = options.ContinuationPromptForegroundColor;
            }
            if (options._continuationPromptBackgroundColor.HasValue)
            {
                Options.ContinuationPromptBackgroundColor = options.ContinuationPromptBackgroundColor;
            }
            if (options._historyNoDuplicates.HasValue)
            {
                Options.HistoryNoDuplicates = options.HistoryNoDuplicates;
                if (Options.HistoryNoDuplicates)
                {
                    _hashedHistory.Clear();
                    _history.OnEnqueue = null;
                    _history.OnDequeue = null;
                    var newHistory = new HistoryQueue<HistoryItem>(Options.MaximumHistoryCount);
                    while (_history.Count > 0)
                    {
                        var item = _history.Dequeue();
                        var itemStr = item._line;
                        if (!_hashedHistory.Contains(itemStr))
                        {
                            newHistory.Enqueue(item);
                            _hashedHistory.Add(itemStr);
                        }
                    }
                    _history = newHistory;
                    _history.OnEnqueue = HistoryOnEnqueueHandler;
                    _history.OnDequeue = HistoryOnDequeueHandler;
                    _currentHistoryIndex = _history.Count;
                }
            }
            if (options._historySearchCursorMovesToEnd.HasValue)
            {
                Options.HistorySearchCursorMovesToEnd = options.HistorySearchCursorMovesToEnd;
            }
            if (options._addToHistoryHandlerSpecified)
            {
                Options.AddToHistoryHandler = options.AddToHistoryHandler;
            }
            if (options._maximumHistoryCount.HasValue)
            {
                Options.MaximumHistoryCount = options.MaximumHistoryCount;
                var newHistory = new HistoryQueue<HistoryItem>(Options.MaximumHistoryCount);
                while (_history.Count > Options.MaximumHistoryCount)
                {
                    _history.Dequeue();
                }
                _history.OnEnqueue = null;
                _history.OnDequeue = null;
                while (_history.Count > 0)
                {
                    newHistory.Enqueue(_history.Dequeue());
                }
                _history = newHistory;
                _history.OnEnqueue = HistoryOnEnqueueHandler;
                _history.OnDequeue = HistoryOnDequeueHandler;
                _currentHistoryIndex = _history.Count;
            }
            if (options._maximumKillRingCount.HasValue)
            {
                Options.MaximumKillRingCount = options.MaximumKillRingCount;
                // TODO - make _killRing smaller
            }
            if (options._editMode.HasValue)
            {
                // Switching modes - clear out chord dispatch table
                _chordDispatchTable.Clear();

                switch (options._editMode)
                {
                case EditMode.Emacs:
                    _dispatchTable = new Dictionary<ConsoleKeyInfo, KeyHandler>(_emacsKeyMap);
                    _dispatchCtrlXTable = new Dictionary<ConsoleKeyInfo, KeyHandler>(_emacsCtrlXMap);
                    _dispatchMetaTable = new Dictionary<ConsoleKeyInfo, KeyHandler>(_emacsMetaMap);

                    _chordDispatchTable = new Dictionary<ConsoleKeyInfo, Dictionary<ConsoleKeyInfo, KeyHandler>>();
                    _chordDispatchTable[Keys.CtrlX] = _dispatchCtrlXTable;
                    _chordDispatchTable[Keys.Escape] = _dispatchMetaTable;
                    break;
#if FALSE
                case EditMode.Vi:
                    //TODO: _dispatchTable = _viKeyMap;
                    break;
#endif
                case EditMode.Windows:
                    _dispatchTable = new Dictionary<ConsoleKeyInfo, KeyHandler>(_cmdKeyMap);
                    _chordDispatchTable = new Dictionary<ConsoleKeyInfo, Dictionary<ConsoleKeyInfo, KeyHandler>>();
                    break;
                }
            }
            if (options._showToolTips.HasValue)
            {
                Options.ShowToolTips = options.ShowToolTips;
            }
            if (options._extraPromptLineCount.HasValue)
            {
                Options.ExtraPromptLineCount = options.ExtraPromptLineCount;
            }
            if (options._dingTone.HasValue)
            {
                Options.DingTone = options.DingTone;
            }
            if (options._dingDuration.HasValue)
            {
                Options.DingDuration = options.DingDuration;
            }
            if (options._bellStyle.HasValue)
            {
                Options.BellStyle = options.BellStyle;
            }
            if (options._completionQueryItems.HasValue)
            {
                Options.CompletionQueryItems = options.CompletionQueryItems;
            }
            if (options.ResetTokenColors)
            {
                Options.ResetColors();
            }
            if (options._tokenKind.HasValue)
            {
                if (options._foregroundColor.HasValue)
                {
                    Options.SetForegroundColor(options.TokenKind, options.ForegroundColor);
                }
                if (options._backgroundColor.HasValue)
                {
                    Options.SetBackgroundColor(options.TokenKind, options.BackgroundColor);
                }
            }
        }

        private void SetKeyHandlerInternal(string[] keys, Action<ConsoleKeyInfo?, object> handler, string briefDescription, string longDescription)
        {
            foreach (var key in keys)
            {
                var chord = ConsoleKeyChordConverter.Convert(key);
                if (chord.Length == 1)
                {
                    _dispatchTable[chord[0]] = MakeKeyHandler(handler, briefDescription, longDescription);
                }
                else
                {
                    _dispatchTable[chord[0]] = MakeKeyHandler(Chord, "ChordFirstKey");
                    Dictionary<ConsoleKeyInfo, KeyHandler> secondDispatchTable;
                    if (!_chordDispatchTable.TryGetValue(chord[0], out secondDispatchTable))
                    {
                        secondDispatchTable = new Dictionary<ConsoleKeyInfo, KeyHandler>();
                        _chordDispatchTable[chord[0]] = secondDispatchTable;
                    }
                    secondDispatchTable[chord[1]] = MakeKeyHandler(handler, briefDescription, longDescription);
                }
            }
        }

        /// <summary>
        /// Helper function for the Set-PSReadlineOption cmdlet.
        /// </summary>
        public static void SetOptions(SetPSReadlineOption options)
        {
            _singleton.SetOptionsInternal(options);
        }

        /// <summary>
        /// Helper function for the Get-PSReadlineOption cmdlet.
        /// </summary>
        public static PSConsoleReadlineOptions GetOptions()
        {
            // Should we copy?  It doesn't matter much, everything can be tweaked from
            // the cmdlet anyway.
            return _singleton._options;
        }

        /// <summary>
        /// Helper function for the Set-PSReadlineKeyHandler cmdlet.
        /// </summary>
        public static void SetKeyHandler(string[] key, Action<ConsoleKeyInfo?, object> handler, string briefDescription, string longDescription)
        {
            _singleton.SetKeyHandlerInternal(key, handler, briefDescription, longDescription);
        }

        /// <summary>
        /// Helper function for the Get-PSReadlineKeyHandler cmdlet.
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<PSConsoleUtilities.KeyHandler> GetKeyHandlers()
        {
            foreach (var entry in _singleton._dispatchTable)
            {
                if (entry.Value.BriefDescription == "Ignore" 
                    || entry.Value.BriefDescription == "ChordFirstKey")
                {
                    continue;
                }
                yield return new PSConsoleUtilities.KeyHandler
                {
                    Key = entry.Key.ToGestureString(),
                    BriefDescription = entry.Value.BriefDescription,
                    LongDescription = entry.Value.LongDescription,
                };
            }

            foreach (var entry in _singleton._chordDispatchTable)
            {
                foreach (var secondEntry in entry.Value)
                {
                    yield return new PSConsoleUtilities.KeyHandler
                    {
                        Key = entry.Key.ToGestureString() + "," + secondEntry.Key.ToGestureString(),
                        BriefDescription = secondEntry.Value.BriefDescription,
                        LongDescription = secondEntry.Value.LongDescription,
                    };
                }
            }
        }

        /// <summary>
        /// Get the state of the buffer - the current input and the position of the cursor
        /// </summary>
        public static void GetBufferState(out string input, out int cursor)
        {
            input = _singleton._buffer.ToString();
            cursor = _singleton._current;
        }

        /// <summary>
        /// Get the state of the buffer - the ast, tokens, errors, and position of the cursor
        /// </summary>
        public static void GetBufferState(out Ast ast, out Token[] tokens, out ParseError[] parseErrors, out int cursor)
        {
            _singleton.ParseInput();
            ast = _singleton._ast;
            tokens = _singleton._tokens;
            parseErrors = _singleton._parseErrors;
            cursor = _singleton._current;
        }

        private static void EnsureIsInitialized()
        {
            // The check that ConsoleBuffer is not null exists mostly
            // for unit tests that may execute this function before
            // the _singleton is initialized.
            if (_singleton.ConsoleBuffer == null)
                _singleton.Initialize();
        }

        /// <summary>
        /// Set the position of the cursor.
        /// </summary>
        public static void SetCursorPosition(int cursor)
        {
            EnsureIsInitialized();

            if (cursor > _singleton._buffer.Length)
            {
                cursor = _singleton._buffer.Length;
            }

            _singleton._current = cursor;
            _singleton.PlaceCursor();
        }
    }

    internal class ConsoleKeyInfoComparer : IEqualityComparer<ConsoleKeyInfo>
    {
        public bool Equals(ConsoleKeyInfo x, ConsoleKeyInfo y)
        {
            return x.Key == y.Key && x.KeyChar == y.KeyChar && x.Modifiers == y.Modifiers;
        }

        public int GetHashCode(ConsoleKeyInfo obj)
        {
            return obj.GetHashCode();
        }
    }

}
