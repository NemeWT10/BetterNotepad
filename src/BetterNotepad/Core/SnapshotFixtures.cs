namespace BetterNotepad.Core;

/// <summary>Sample files for snapshot mode (written under the test data folder; nothing outside the project is ever read).</summary>
internal static class SnapshotFixtures
{
    public static readonly (string Name, string Text)[] All =
    [
        ("NoteBook.cs", Cs),
        ("tools.py", Py),
        ("README.md", Md),
        ("config.json", Json),
        ("server.log", Log),
        ("boot.asm", Asm),
    ];

    private const string Cs = """
        // BetterNotepad snapshot fixture (C#)
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace Demo.Notes;

        /// <summary>Keeps a list of notes and searches them.</summary>
        [Serializable]
        public sealed class NoteBook : IDisposable
        {
            private const int MaxNotes = 128;
            private readonly List<string> _notes = new();

            public string Title { get; set; } = "Daily notes";

            public event EventHandler<string>? Added;

            public bool TryAdd(string text, out int index)
            {
                index = -1;
                if (string.IsNullOrWhiteSpace(text) || _notes.Count >= MaxNotes) return false;
                _notes.Add(text.Trim());
                index = _notes.Count - 1;
                Added?.Invoke(this, text);
                return true;
            }

            public IEnumerable<string> Search(string needle, bool ignoreCase = true)
            {
                var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                foreach (var note in _notes)
                    if (note.Contains(needle, cmp)) yield return note;
            }

            #region io
            public async Task<int> SaveAsync(string path)
            {
                var lines = _notes.Select((n, i) => $"{i + 1:000}: {n}").ToArray();
                await File.WriteAllLinesAsync(path, lines);
                return lines.Length; /* 0x1F, 3.14, 'x' */
            }
            #endregion

            public override string ToString() => @"NoteBook ""x"" " + Title;

            public void Dispose() => _notes.Clear();
        }
        """;

    private const string Py = """"
        #!/usr/bin/env python3
        """Small helpers for the demo project."""
        import json
        import re
        from dataclasses import dataclass, field
        from pathlib import Path

        PATTERN = re.compile(r"\b(?P<key>[a-z_]+)=(?P<value>\d+)\b")


        @dataclass
        class Note:
            title: str
            tags: list[str] = field(default_factory=list)
            priority: int = 3  # 1 = urgent

            def is_urgent(self) -> bool:
                return self.priority <= 1 or "urgent" in self.tags


        def load(path: Path) -> list[Note]:
            """Read notes from a JSON file; missing file means no notes."""
            if not path.exists():
                return []
            with path.open(encoding="utf-8") as fh:
                return [Note(**item) for item in json.load(fh)]


        def main() -> None:
            notes = load(Path("notes.json"))
            for n in sorted(notes, key=lambda n: (n.priority, n.title)):
                print(f"{n.title!r:<24} {'!' if n.is_urgent() else ' '} {len(n.tags)} tags")


        if __name__ == "__main__":
            main()
        """";

    private const string Md = """
        # BetterNotepad demo

        A **fast**, _quiet_ notepad for people who live in text. See the [docs](https://example.com/docs) or `Ctrl+Shift+P`.

        ## Features

        - Tabs that survive a restart
        - Multi-cursor editing (`Alt+Click`)
        - [x] Markdown preview
        - [ ] Plugins (not planned)

        > Quote: keep it simple.

        ### Code

        ```csharp
        var notes = new NoteBook();
        notes.TryAdd("hello", out _);
        ```

        | Shortcut | Action        |
        |----------|---------------|
        | Ctrl+P   | Quick open    |
        | Ctrl+G   | Go to line    |

        1. First
        2. Second

        ---
        Written in *Markdown*.
        """;

    private const string Json = """
        {
          "name": "betternotepad-demo",
          "version": "1.4.0",
          "private": true,
          "editor": {
            "fontFamily": "Cascadia Mono",
            "fontSize": 11.5,
            "tabSize": 4,
            "wordWrap": false,
            "rulers": [80, 120],
            "theme": null
          },
          "files": ["*.cs", "*.py", "docs/**/*.md"],
          "limits": { "maxFileMb": 50, "autosaveSeconds": 0 }
        }
        """;

    private const string Log = """
        2026-10-12 08:14:02.113 INFO  Server starting on port 8080
        2026-10-12 08:14:02.540 DEBUG Loaded 42 routes from routes.json
        2026-10-12 08:14:03.001 WARN  Cache directory is almost full (91%)
        2026-10-12 08:14:07.874 INFO  GET /api/notes 200 12ms
        2026-10-12 08:14:09.215 ERROR Unhandled exception in handler /api/save
        System.IO.IOException: The process cannot access the file 'notes.json' because it is being used by another process.
           at Demo.Notes.NoteBook.SaveAsync(String path) in NoteBook.cs:line 51
        2026-10-12 08:14:09.220 FATAL Shutting down worker 3
        2026-10-12 08:14:10.002 TRACE heartbeat ok
        2026-10-12 08:14:11.348 INFO  Reconnected to database db01 (10.0.0.12:5432)
        """;

    private const string Asm = """
        ; boot.asm - NASM, x86-64 Linux
        section .data
            msg     db  "Hello, notepad!", 10, 0
            len     equ $ - msg
            counter dq  0x1F

        section .text
            global _start

        _start:
            mov     rax, 1              ; sys_write
            mov     rdi, 1              ; stdout
            lea     rsi, [rel msg]
            mov     rdx, len
            syscall

            xor     rcx, rcx
        .loop:
            inc     rcx
            cmp     rcx, 10
            jne     .loop

            mov     rax, 60             ; sys_exit
            xor     edi, edi
            syscall
        """;
}
