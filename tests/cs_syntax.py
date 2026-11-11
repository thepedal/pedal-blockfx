# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Pedal
# Syntax-check C# files with tree-sitter (modern grammar). Exit 1 if any ERROR/missing node.
import sys
import tree_sitter_c_sharp as tscs
from tree_sitter import Language, Parser
p = Parser(Language(tscs.language()))
bad = 0
for path in sys.argv[1:]:
    src = open(path, 'rb').read(); found = []
    def walk(n):
        if n.type == 'ERROR' or n.is_missing: found.append((n.start_point[0] + 1, n.type))
        for c in n.children: walk(c)
    walk(p.parse(src).root_node)
    print(f"{path}: {len(found)} syntax errors", *[f"\n  line {l}: {t}" for l, t in found[:10]])
    bad += len(found)
sys.exit(1 if bad else 0)
