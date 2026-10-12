using BetterNotepad.Editor.Highlighting;

namespace BetterNotepad.Tests;

public sealed class LanguagesTests
{
    // ---------------------------------------------------------------- ForFileName

    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("script.csx", "csharp")]
    [InlineData("util.h", "c")]
    [InlineData("main.c", "c")]
    [InlineData("util.hpp", "cpp")]
    [InlineData("main.cpp", "cpp")]
    [InlineData("app.ts", "typescript")]
    [InlineData("view.tsx", "typescript")]
    [InlineData("app.js", "javascript")]
    [InlineData("view.jsx", "javascript")]
    [InlineData("build.ps1", "powershell")]
    [InlineData("ci.yml", "yaml")]
    [InlineData("ci.yaml", "yaml")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("dockerfile", "dockerfile")]
    [InlineData("CMakeLists.txt", "cmake")]
    [InlineData("Makefile", "makefile")]
    [InlineData("makefile", "makefile")]
    [InlineData("logo.svg", "xml")]
    [InlineData("LOGO.SVG", "xml")]
    [InlineData(".gitignore", "ini")]
    [InlineData("data.json", "json")]
    [InlineData("README.md", "markdown")]
    [InlineData("notes.txt", "text")]
    [InlineData("run.bat", "batch")]
    [InlineData("run.sh", "shell")]
    [InlineData("main.py", "python")]
    [InlineData("main.go", "go")]
    [InlineData("main.rs", "rust")]
    [InlineData("query.sql", "sql")]
    [InlineData("page.html", "html")]
    [InlineData("site.css", "css")]
    [InlineData("a.csproj", "xml")]
    [InlineData("server.log", "log")]
    [InlineData("data.csv", "csv")]
    [InlineData("boot.asm", "asm-x86")]
    public void ForFileName_KnownFiles(string name, string expected)
        => Assert.Equal(expected, Languages.ForFileName(name));

    [Theory]
    [InlineData("file.unknownext")]
    [InlineData("README")]
    [InlineData("noextension")]
    [InlineData("archive.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ForFileName_Unknown_IsPlainText(string? name)
        => Assert.Equal("text", Languages.ForFileName(name));

    [Fact]
    public void ForFileName_UsesOnlyTheFileNamePart_AndIsCaseInsensitive()
    {
        Assert.Equal("csharp", Languages.ForFileName(@"C:\Some Dir.d\sub\Program.CS"));
        Assert.Equal("makefile", Languages.ForFileName(@"C:\src.cs\Makefile"));
        Assert.Equal("cmake", Languages.ForFileName(@"C:\proj\cmakelists.TXT"));
        Assert.Equal("text", Languages.ForFileName(@"C:\dir.cs\README"));
    }

    // ---------------------------------------------------------------- registry

    [Fact]
    public void Registry_HasUniqueIds_AndEveryLanguageIsReachable()
    {
        var ids = Languages.List.Select(l => l.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var l in Languages.List)
        {
            Assert.Same(l, Languages.Get(l.Id));
            Assert.False(string.IsNullOrWhiteSpace(l.Name));
            Assert.All(l.Extensions, e => Assert.StartsWith(".", e));
            Assert.StartsWith(".", l.DefaultExtension);
        }
    }

    [Fact]
    public void Get_UnknownOrNull_FallsBackToPlainText()
    {
        Assert.Equal("text", Languages.Get(null).Id);
        Assert.Equal("text", Languages.Get("no-such-language").Id);
        Assert.Equal("csharp", Languages.Get("CSHARP").Id);
        Assert.False(Languages.Get("text").IsCode);
    }

    [Fact]
    public void RequiredLanguagesAreRegistered()
    {
        string[] required =
        [
            "c", "cpp", "csharp", "java", "python", "javascript", "typescript", "html", "css", "scss", "php",
            "xml", "json", "yaml", "toml", "ini", "sql", "shell", "batch", "powershell", "go", "rust", "kotlin",
            "lua", "ruby", "markdown", "text", "log", "asm-x86", "asm-arm", "asm-avr",
        ];
        var known = Languages.List.Select(l => l.Id).ToHashSet();
        var missing = required.Except(known).ToList();
        Assert.Empty(missing);
    }

    // ---------------------------------------------------------------- Detect

    [Fact]
    public void Detect_EmptyAndWhitespace_IsText()
    {
        Assert.Equal("text", Languages.Detect(""));
        Assert.Equal("text", Languages.Detect("   \r\n\t  "));
    }

    [Theory]
    [InlineData("{\"name\": \"x\", \"items\": [1, 2, 3], \"ok\": true}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("  \n{\n  \"a\": {\"b\": null}\n}\n")]
    [InlineData("{\n  // comment\n  \"a\": 1,\n}")]
    [InlineData("{\"truncated\": [1, 2,")]
    public void Detect_Json(string text)
        => Assert.Equal("json", Languages.Detect(text));

    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root><item id=\"1\"/></root>")]
    [InlineData("<root>\n  <item>1</item>\n</root>\n")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup/>\n</Project>")]
    public void Detect_Xml(string text)
        => Assert.Equal("xml", Languages.Detect(text));

    [Theory]
    [InlineData("<!DOCTYPE html>\n<html lang=\"en\">\n<head><title>x</title></head>\n<body></body>\n</html>")]
    [InlineData("<html>\n<body><p>hi</p></body>\n</html>")]
    [InlineData("<!doctype HTML>\n<p>hi</p>")]
    public void Detect_Html(string text)
        => Assert.Equal("html", Languages.Detect(text));

    [Fact]
    public void Detect_CSharp()
    {
        const string code = """
            using System;
            using System.Collections.Generic;

            namespace Demo
            {
                public class Program
                {
                    public static void Main(string[] args)
                    {
                        var list = new List<int>();
                        Console.WriteLine(list.Count);
                    }
                }
            }
            """;
        Assert.Equal("csharp", Languages.Detect(code));
    }

    [Fact]
    public void Detect_Java()
    {
        const string code = """
            package com.example.demo;

            import java.util.List;

            public class Main {
                public static void main(String[] args) {
                    System.out.println("Hello");
                }
            }
            """;
        Assert.Equal("java", Languages.Detect(code));
    }

    [Fact]
    public void Detect_Python()
    {
        const string code = """
            import os
            import sys

            def main(argv: list) -> int:
                print("args:", argv)
                return 0

            if __name__ == "__main__":
                sys.exit(main(sys.argv))
            """;
        Assert.Equal("python", Languages.Detect(code));
    }

    [Fact]
    public void Detect_Sql()
    {
        const string code = """
            SELECT u.id, u.name
            FROM users u
            JOIN orders o ON o.user_id = u.id
            WHERE o.total > 100;

            INSERT INTO log (msg) VALUES ('done');
            """;
        Assert.Equal("sql", Languages.Detect(code));
    }

    [Fact]
    public void Detect_Markdown()
    {
        const string text = """
            # Project title

            Some intro with a [link](https://example.com).

            ## Tasks

            - [ ] first
            - [x] second

            > a quote
            """;
        Assert.Equal("markdown", Languages.Detect(text));
    }

    [Fact]
    public void Detect_LogLines()
    {
        const string text = """
            2026-10-12 10:11:12,123 INFO  Starting service
            2026-10-12 10:11:13,456 WARN  Disk space low
            2026-10-12 10:11:14,789 ERROR Request failed: timeout
            """;
        Assert.Equal("log", Languages.Detect(text));
    }

    [Theory]
    [InlineData("#!/bin/bash\necho hello\n", "shell")]
    [InlineData("#!/bin/sh\nset -e\n", "shell")]
    [InlineData("#!/usr/bin/env bash\nls\n", "shell")]
    [InlineData("#!/usr/bin/env python3\nprint('x')\n", "python")]
    [InlineData("#!/usr/bin/python\n", "python")]
    [InlineData("#!/usr/bin/env node\nconsole.log(1)\n", "javascript")]
    [InlineData("#!/usr/bin/env pwsh\nWrite-Host hi\n", "powershell")]
    [InlineData("#!/usr/bin/env ruby\nputs 1\n", "ruby")]
    public void Detect_Shebang(string text, string expected)
        => Assert.Equal(expected, Languages.Detect(text));

    [Fact]
    public void Detect_ShellScriptWithShebang_WinsOverContent()
    {
        const string code = "#!/bin/bash\nimport os\ndef x(): pass\n";
        Assert.Equal("shell", Languages.Detect(code));
    }

    [Fact]
    public void Detect_Php_Diff_Batch_Css_Yaml()
    {
        Assert.Equal("php", Languages.Detect("<?php\necho 'hi';\n"));
        Assert.Equal("diff", Languages.Detect("diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n"));
        Assert.Equal("batch", Languages.Detect("@echo off\nset NAME=world\necho Hello %NAME%\ngoto end\n:end\n"));
        Assert.Equal("css", Languages.Detect("body {\n  margin: 0;\n}\n.a, .b > p {\n  color: red;\n}\n"));
        Assert.Equal("yaml", Languages.Detect("name: ci\non: push\njobs:\n  build:\n    runs-on: ubuntu\n    steps:\n      - name: checkout\n"));
    }

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    [InlineData("Hello,\nthis is a short note.\nSee you tomorrow!")]
    [InlineData("Zakupy: mleko, chleb, masło.\nZadzwonić do mamy.")]
    [InlineData("Meeting notes\nbuy milk and then call John")]
    [InlineData("42")]
    [InlineData("just one word")]
    public void Detect_ShortPlainProse_IsText(string text)
        => Assert.Equal("text", Languages.Detect(text));

    [Fact]
    public void Detect_OnlyLooksAtTheBeginningOfHugeText()
    {
        var prose = string.Concat(Enumerable.Repeat("some plain prose without anything special. ", 1000));
        Assert.Equal("text", Languages.Detect(prose));
    }

    // ---------------------------------------------------------------- ASM dialects

    [Fact]
    public void AsmDialect_X86_Nasm()
    {
        const string code = """
            section .text
            global _start
            _start:
                mov eax, 1
                mov ebx, 0
                xor ecx, ecx
                push ebp
                int 0x80
            """;
        Assert.Equal("asm-x86", Languages.DetectAsmDialect(code));
    }

    [Fact]
    public void AsmDialect_X86_Masm_64bit()
    {
        const string code = """
            .code
            main PROC
                mov rax, 5
                add rax, rbx
                lea rdi, [rsp+8]
                call ExitProcess
            main ENDP
            """;
        Assert.Equal("asm-x86", Languages.DetectAsmDialect(code));
    }

    [Fact]
    public void AsmDialect_X86_GasAtt()
    {
        const string code = """
            .globl main
            main:
                pushl %ebp
                movl %esp, %ebp
                movl $1, %eax
                popl %ebp
                ret
            """;
        Assert.Equal("asm-x86", Languages.DetectAsmDialect(code));
    }

    [Fact]
    public void AsmDialect_Arm()
    {
        const string code = """
            .arm
            .global _start
            _start:
                mov r0, #1
                ldr r1, =msg
                mov r2, #13
                bl printf
                bx lr
            """;
        Assert.Equal("asm-arm", Languages.DetectAsmDialect(code));
    }

    [Fact]
    public void AsmDialect_Arm64()
    {
        const string code = """
            .global _start
            _start:
                mov x0, #1
                ldr x1, =msg
                mov w2, #13
                svc #0
                ret
            """;
        Assert.Equal("asm-arm", Languages.DetectAsmDialect(code));
    }

    [Fact]
    public void AsmDialect_Avr()
    {
        const string code = """
            .include "m328Pdef.inc"
            .org 0x0000
                rjmp main
            main:
                ldi r16, 0xFF
                out DDRB, r16
            loop:
                sbi PORTB, 0
                rjmp loop
            """;
        Assert.Equal("asm-avr", Languages.DetectAsmDialect(code));
    }

    [Fact]
    public void AsmDialect_NothingRecognizable_DefaultsToX86()
        => Assert.Equal("asm-x86", Languages.DetectAsmDialect("hello world"));

    [Fact]
    public void Detect_ArmAsmSnippet_IsAsmArm()
    {
        const string code = """
            .arm
            _start:
                mov r0, #1
                ldr r1, =msg
                mov r2, #13
                bl printf
                bx lr
            """;
        Assert.Equal("asm-arm", Languages.Detect(code));
    }

    [Fact]
    public void Detect_X86AsmSnippet_IsAsmX86()
    {
        const string code = """
            section .text
            global _start
            _start:
                mov eax, 1
                mov ebx, 0
                int 0x80
            """;
        Assert.Equal("asm-x86", Languages.Detect(code));
    }
}
