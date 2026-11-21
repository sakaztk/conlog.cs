```
Usage: conlog [options] [--] [command [args...]]

Runs the command in a pseudo console and logs everything it prints.
Without a command, a new shell (same kind as the parent shell) is started
in the current console and logged. If stdin or stdout is redirected, the
command runs in pipe mode (no pseudo console) and its output is logged.

Log formats:
  raw (default)  Saves the output exactly as the program emitted it, escape
                 sequences included. It is written immediately, so nothing is
                 lost even if conlog is killed, and it can be followed live
                 (e.g. Get-Content -Wait). Programs that redraw the screen
                 (PSReadLine, progress bars, ...) make it hard to read as text.
  text (--text)  Reproduces the screen with a built-in terminal emulator and
                 saves clean text without escape sequences. A row is written
                 only when it is final: when it scrolls off the screen, when
                 the screen is cleared, or when the session ends. Rows still
                 on screen are lost if conlog is killed.
  --raw-too      Use with --text to also keep the raw log as <log file>.raw,
                 a complete and immediate record next to the readable one.
                 (No effect without --text, since raw is already the default.)

Options (sorted by long name):
  -a, --append               append to the log file
  -e, --encoding <name>      log file encoding: utf8 (default), utf8bom, utf16, utf16be,
                             sjis, or any .NET encoding name
  -h, --help                 show this help
  -F, --no-footer            do not write the footer line
  -H, --no-header            do not write the header lines
  -o, --output <file>        log file (default: conlog_yyyyMMdd_HHmmss.log)
  -r, --raw-too              also write the raw log to <log file>.raw (use with --text)
  -E, --src-encoding <name>  encoding of the child's output in pipe mode: utf8 (default),
                             oem, ansi, sjis, ... (ignored in pseudo console mode)
  -t, --text                 save the readable text format instead of raw (alias: --strip)
  -s, --timestamp            prefix each line with a timestamp (implies --text) (alias: --ts)

Short options can be bundled (-at) and take their value attached or separate
(-ofile.log, -o file). Long options also accept --name=value.
```
