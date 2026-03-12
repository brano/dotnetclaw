// ============================================================================
//  terminal-interop.js — xterm.js ↔ Blazor bridge for DotnetClaw Web Terminal
//
//  Requires (loaded before this script):
//    • xterm@5.x        → window.Terminal
//    • xterm-addon-fit  → window.FitAddon  (object with .FitAddon class)
// ============================================================================

/* eslint-disable no-undef */

let _term       = null;
let _fitAddon   = null;
let _dotNetRef  = null;
let _lineBuffer = '';
let _history    = [];   // command history
let _histIdx    = -1;   // current history position (-1 = typing new)
let _savedLine  = '';   // preserves the partial line during history navigation
let _initialized = false;

// ── Public API (called from C# via IJSRuntime) ─────────────────────────────

/**
 * Mount and configure the xterm.js terminal.
 * @param {string} elementId  - ID of the DOM element to mount into
 * @param {object} dotNetRef  - DotNetObjectReference for invoking C# callbacks
 */
window.terminalInit = function (elementId, dotNetRef) {
    if (_initialized) return;
    _initialized = true;
    _dotNetRef   = dotNetRef;

    _term = new Terminal({
        fontFamily:   '"JetBrains Mono", "Cascadia Code", "Fira Code", Consolas, monospace',
        fontSize:     14,
        lineHeight:   1.5,
        cursorBlink:  true,
        cursorStyle:  'block',
        scrollback:   10000,
        convertEol:   false,   // renderer normalises to \r\n explicitly
        allowProposedApi: true,
        theme: {
            background:    '#020817',   // matches app bg hsl(224 71% 4%)
            foreground:    '#e2e8f0',
            cursor:        '#3b82f6',
            cursorAccent:  '#020817',
            selectionBackground: 'rgba(59,130,246,0.3)',
            black:         '#1e293b',
            red:           '#ef4444',
            green:         '#22c55e',
            yellow:        '#eab308',
            blue:          '#3b82f6',
            magenta:       '#a855f7',
            cyan:          '#06b6d4',
            white:         '#e2e8f0',
            brightBlack:   '#475569',
            brightRed:     '#f87171',
            brightGreen:   '#4ade80',
            brightYellow:  '#facc15',
            brightBlue:    '#60a5fa',
            brightMagenta: '#c084fc',
            brightCyan:    '#22d3ee',
            brightWhite:   '#f8fafc',
        },
    });

    _fitAddon = new FitAddon.FitAddon();
    _term.loadAddon(_fitAddon);

    const container = document.getElementById(elementId);
    if (!container) {
        console.error('[terminal-interop] container not found:', elementId);
        return;
    }

    _term.open(container);
    _fitAddon.fit();

    // Responsive resize via ResizeObserver
    if (typeof ResizeObserver !== 'undefined') {
        const ro = new ResizeObserver(() => {
            try { _fitAddon?.fit(); } catch { /* ignore */ }
        });
        ro.observe(container);
    }

    _term.onData(handleInput);
    _term.focus();
};

/**
 * Write ANSI/VT100 encoded text to the terminal.
 * @param {string} data
 */
window.terminalWrite = function (data) {
    if (_term) _term.write(data);
};

/**
 * Give keyboard focus to the terminal.
 */
window.terminalFocus = function () {
    _term?.focus();
};

/**
 * Clean up the terminal instance (called on Blazor component dispose).
 */
window.terminalDispose = function () {
    try { _term?.dispose(); } catch { /* ignore */ }
    _term        = null;
    _fitAddon    = null;
    _dotNetRef   = null;
    _initialized = false;
    _lineBuffer  = '';
    _history     = [];
    _histIdx     = -1;
};

// ── Input handler ──────────────────────────────────────────────────────────

function handleInput(data) {
    // ── Control sequences ────────────────────────────────────────────────────

    // Ctrl+C — cancel
    if (data === '\x03') {
        _dotNetRef?.invokeMethodAsync('CancelInput');
        _term.write('^C\r\n');
        _lineBuffer = '';
        _histIdx    = -1;
        return;
    }

    // Ctrl+L — clear screen
    if (data === '\x0c') {
        _term.write('\x1b[2J\x1b[H');
        return;
    }

    // Ctrl+U — erase line
    if (data === '\x15') {
        eraseLine(_lineBuffer.length);
        _lineBuffer = '';
        return;
    }

    // Ctrl+W — erase last word
    if (data === '\x17') {
        const trimmed = _lineBuffer.trimEnd();
        const lastSpace = trimmed.lastIndexOf(' ');
        const newLine = lastSpace >= 0 ? trimmed.slice(0, lastSpace + 1) : '';
        const toErase = _lineBuffer.length - newLine.length;
        eraseLine(toErase);
        _lineBuffer = newLine;
        return;
    }

    // ── Arrow keys ──────────────────────────────────────────────────────────

    // Up arrow — previous history entry
    if (data === '\x1b[A') {
        if (_history.length === 0) return;
        if (_histIdx === -1) {
            _savedLine = _lineBuffer;
            _histIdx = _history.length - 1;
        } else if (_histIdx > 0) {
            _histIdx--;
        }
        replaceLineWith(_history[_histIdx]);
        return;
    }

    // Down arrow — next history entry
    if (data === '\x1b[B') {
        if (_histIdx === -1) return;
        if (_histIdx < _history.length - 1) {
            _histIdx++;
            replaceLineWith(_history[_histIdx]);
        } else {
            _histIdx = -1;
            replaceLineWith(_savedLine);
        }
        return;
    }

    // Other escape sequences (right/left arrows, F-keys, etc.) — ignore
    if (data.startsWith('\x1b')) return;

    // ── Enter — submit ───────────────────────────────────────────────────────
    if (data === '\r') {
        const line = _lineBuffer;
        _lineBuffer = '';
        _histIdx    = -1;
        _term.write('\r\n');

        if (line.trim().length > 0) {
            // Deduplicate consecutive identical entries
            if (_history.length === 0 || _history[_history.length - 1] !== line) {
                _history.push(line);
                if (_history.length > 200) _history.shift(); // bounded
            }
        }

        _dotNetRef?.invokeMethodAsync('HandleInputLine', line);
        return;
    }

    // ── Backspace ────────────────────────────────────────────────────────────
    if (data === '\x7f') {
        if (_lineBuffer.length > 0) {
            _lineBuffer = _lineBuffer.slice(0, -1);
            _term.write('\b \b');
        }
        return;
    }

    // ── Tab — no-op (future autocomplete) ───────────────────────────────────
    if (data === '\t') return;

    // ── Printable characters ─────────────────────────────────────────────────
    if (data.charCodeAt(0) >= 32) {
        _lineBuffer += data;
        _term.write(data);
    }
}

// ── Helpers ────────────────────────────────────────────────────────────────

/** Erase `count` characters to the left of the cursor. */
function eraseLine(count) {
    if (count <= 0) return;
    _term.write('\b'.repeat(count) + ' '.repeat(count) + '\b'.repeat(count));
}

/** Replace the currently displayed input line with `text`. */
function replaceLineWith(text) {
    eraseLine(_lineBuffer.length);
    _lineBuffer = text;
    _term.write(text);
}
