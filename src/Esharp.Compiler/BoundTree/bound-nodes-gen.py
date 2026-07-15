#!/usr/bin/env python3
"""
bound-nodes-gen.py — regenerate BoundTreeVisitor.cs, BoundTreeRewriter.cs,
and BoundNodes.Update.cs from bound-nodes.schema.json.

Usage (from this directory):
    python3 bound-nodes-gen.py

The three output files are written next to this script (overwrite in place).

Design notes
------------
The schema drives everything. The generator is intentionally dumb — it
iterates the node list in schema order, dispatches on child_kind, and
emits the three files verbatim. The existing hand-written files ARE the
golden output; re-running this script after any schema edit must reproduce
them exactly (aside from whitespace) so the generator can be verified by
diffing.

child_kind enum → emit action (Visitor / Rewriter):
  expr              → VisitExpression(node.F) / RewriteExpression(node.F)
  call_expr         → VisitExpression(node.F) / (BoundCallExpression)RewriteExpression(node.F)
  stmt              → VisitStatement(node.F)  / RewriteStatement(node.F)
  block             → VisitBlockStatement(node.F) / RewriteBlock(node.F)
  expr_list         → foreach + VisitExpression / RewriteExpressions helper
  stmt_list         → foreach + VisitStatement  / RewriteStatements helper
  field_init_list   → foreach + VisitExpression(f.Value) / RewriteFieldInits helper
  member            → VisitMember / VisitFunctionDeclaration loop (special-cased per node)
  catch_list        → inline per-clause Body+Guard (special)
  match_arm_list    → inline per-arm Guard?+Body (special)
  match_expr_arm_list → inline per-arm Guard?+Value (special)
  select_arm_list   → inline per-arm Channel?+Value?+Body (special)
  interp_part_list  → inline per-part Expr? (special)
  if_expr_branch_list → inline per-branch Condition+Body+Value? (special)
"""

from __future__ import annotations
import json, os, sys
from pathlib import Path
from typing import Any

SCHEMA_PATH = Path(__file__).parent / "bound-nodes.schema.json"
OUT_DIR     = Path(__file__).parent

# C# reserved keywords — a camelCased field name that lands on one of these (e.g.
# field `Else` -> `else`) must be emitted as a verbatim identifier (`@else`).
CSHARP_KEYWORDS = frozenset("""
abstract as base bool break byte case catch char checked class const continue decimal
default delegate do double else enum event explicit extern false finally fixed float for
foreach goto if implicit in int interface internal is lock long namespace new null object
operator out override params private protected public readonly ref return sbyte sealed
short sizeof stackalloc static string struct switch this throw true try typeof uint ulong
unchecked unsafe ushort using virtual void volatile while
""".split())

def param_name(field_name: str) -> str:
    """camelCase a PascalCase field name into a parameter name, escaping C# keywords."""
    pname = field_name[0].lower() + field_name[1:]
    return "@" + pname if pname in CSHARP_KEYWORDS else pname

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def load_schema() -> dict[str, Any]:
    with SCHEMA_PATH.open() as f:
        return json.load(f)

def nodes_of_category(schema: dict, *categories: str) -> list[dict]:
    return [n for n in schema["nodes"]
            if "name" in n and n.get("category") in categories]

def rewriter_nodes(schema: dict) -> list[dict]:
    """Nodes that have at least one child_kind field (visitor/rewriter entries)."""
    return [n for n in schema["nodes"]
            if "name" in n and "fields" in n
            and n.get("category") not in ("Supporting", None)]

def update_nodes(schema: dict) -> list[dict]:
    """Nodes that have at least one update=True field."""
    return [n for n in schema["nodes"]
            if "name" in n and "fields" in n
            and any(f.get("update") for f in n.get("fields", []))]

# ---------------------------------------------------------------------------
# Visitor emission helpers
# ---------------------------------------------------------------------------

def emit_visitor_void_body(n: dict) -> list[str]:
    """Return lines for the void-visitor protected virtual method body."""
    name = n["name"]
    fields = n.get("fields", [])
    lines: list[str] = []

    child_fields = [(f["name"], f["child_kind"], f.get("nullable", False))
                    for f in fields if f.get("child_kind")]

    if not child_fields:
        return []  # leaf — empty body, no override needed (default is { })

    for fname, ck, nullable in child_fields:
        if ck in ("expr", "call_expr"):
            if nullable:
                lines.append(f"        if (node.{fname} is {{}} _{fname[0].lower()}) VisitExpression(_{fname[0].lower()});")
            else:
                lines.append(f"        VisitExpression(node.{fname});")
        elif ck == "stmt":
            if nullable:
                lines.append(f"        if (node.{fname} is {{}} _{fname[0].lower()}) VisitStatement(_{fname[0].lower()});")
            else:
                lines.append(f"        VisitStatement(node.{fname});")
        elif ck == "block":
            lines.append(f"        VisitBlockStatement(node.{fname});")
        elif ck == "expr_list":
            if nullable:
                lines.append(f"        if (node.{fname} is {{}} _{fname[0].lower()}_list)")
                lines.append(f"            foreach (var _e in _{fname[0].lower()}_list) VisitExpression(_e);")
            else:
                lines.append(f"        foreach (var _e in node.{fname}) VisitExpression(_e);")
        elif ck == "stmt_list":
            lines.append(f"        foreach (var _s in node.{fname}) VisitStatement(_s);")
        elif ck == "field_init_list":
            lines.append(f"        foreach (var _f in node.{fname}) VisitExpression(_f.Value);")
        elif ck == "member":
            # Depends on field type
            ftype = next(f["type"] for f in fields if f["name"] == fname)
            if "FunctionDeclaration" in ftype:
                lines.append(f"        foreach (var _m in node.{fname}) VisitFunctionDeclaration(_m);")
            elif "InitDeclaration" in ftype:
                lines.append(f"        if (node.{fname} is {{}} _{fname[0].lower()}_list)")
                lines.append(f"            foreach (var _i in _{fname[0].lower()}_list) VisitInitDeclaration(_i);")
            else:
                lines.append(f"        foreach (var _m in node.{fname}) VisitMember(_m);")
        elif ck == "catch_list":
            lines += [
                f"        VisitBlockStatement(node.Body);",
                f"        foreach (var _c in node.Catches)",
                f"        {{",
                f"            VisitBlockStatement(_c.Body);",
                f"            if (_c.Guard is {{}} _g) VisitExpression(_g);",
                f"        }}",
            ]
        elif ck == "match_arm_list":
            lines += [
                f"        VisitExpression(node.Subject);",
                f"        foreach (var _arm in node.Arms)",
                f"        {{",
                f"            if (_arm.Guard is {{}} _g) VisitExpression(_g);",
                f"            VisitBlockStatement(_arm.Body);",
                f"        }}",
            ]
        elif ck == "match_expr_arm_list":
            lines += [
                f"        VisitExpression(node.Subject);",
                f"        foreach (var _arm in node.Arms)",
                f"        {{",
                f"            if (_arm.Guard is {{}} _g) VisitExpression(_g);",
                f"            VisitExpression(_arm.Value);",
                f"        }}",
            ]
        elif ck == "select_arm_list":
            lines += [
                f"        foreach (var _arm in node.Arms)",
                f"        {{",
                f"            if (_arm.Channel is {{}} _ch) VisitExpression(_ch);",
                f"            if (_arm.Value   is {{}} _v)  VisitExpression(_v);",
                f"            VisitBlockStatement(_arm.Body);",
                f"        }}",
            ]
        elif ck == "interp_part_list":
            lines += [
                f"        foreach (var _p in node.Parts)",
                f"            if (_p.Expr is {{}} _e) VisitExpression(_e);",
            ]
        elif ck == "if_expr_branch_list":
            lines += [
                f"        foreach (var _b in node.Branches)",
                f"        {{",
                f"            VisitExpression(_b.Condition);",
                f"            foreach (var _s in _b.Body) VisitStatement(_s);",
                f"            if (_b.Value is {{}} _v) VisitExpression(_v);",
                f"        }}",
                f"        foreach (var _s in node.ElseBody) VisitStatement(_s);",
                f"        if (node.ElseValue is {{}} _ev) VisitExpression(_ev);",
            ]

    return lines


# ---------------------------------------------------------------------------
# Generate BoundTreeVisitor.cs
# ---------------------------------------------------------------------------

VISITOR_HEADER = """\
// ============================================================================
// BoundTreeVisitor — abstract visitor + concrete no-op default visitor.
//
// GENERATED by bound-nodes-gen.py from bound-nodes.schema.json.
// DO NOT EDIT by hand — edit the schema and re-run the generator.
//
// Usage:
//   Derive from BoundTreeVisitor<T> for a fold (each Visit returns T).
//   Derive from BoundTreeVisitor for a side-effect walk (returns void).
//
// CORE and FEATURE nodes are both listed; a Lowering pass visitor can assert
// that no FEATURE nodes are present by overriding the FEATURE visits to throw.
// ============================================================================

namespace Esharp.BoundTree;

"""

def gen_visitor(schema: dict) -> str:
    lines = [VISITOR_HEADER]

    # ---- Void visitor ----
    lines.append("// ---------------------------------------------------------------------------")
    lines.append("// Void visitor (side-effect walk)")
    lines.append("// ---------------------------------------------------------------------------")
    lines.append("")
    lines.append("public abstract class BoundTreeVisitor")
    lines.append("{")
    lines.append("    // Central dispatch. Subclasses rarely override this — override the")
    lines.append("    // typed Visit methods below instead.")
    lines.append("    public virtual void VisitNode(BoundNode node)")
    lines.append("    {")
    lines.append("        switch (node)")
    lines.append("        {")

    # Emit switch cases grouped by category/tier
    all_dispatchable = [n for n in schema["nodes"]
                        if "name" in n and "fields" in n
                        and n.get("base") in ("BoundNode", "BoundMember", "BoundStatement", "BoundExpression")]

    top_level     = [n for n in all_dispatchable if n["category"] == "TopLevel" and n["name"] != "BoundUsing"]
    members       = [n for n in all_dispatchable if n["category"] == "Member"]
    stmts_core    = [n for n in all_dispatchable if n["category"] == "Statement" and n["tier"] == "CORE"]
    stmts_feature = [n for n in all_dispatchable if n["category"] == "Statement" and n["tier"] == "FEATURE"]
    exprs_core    = [n for n in all_dispatchable if n["category"] == "Expression" and n["tier"] == "CORE"]
    exprs_feature = [n for n in all_dispatchable if n["category"] == "Expression" and n["tier"] == "FEATURE"]

    def visit_name(node_name: str) -> str:
        # Strip "Bound" prefix for the method name
        return node_name[len("Bound"):]

    def emit_cases(nodes: list[dict], comment: str) -> None:
        lines.append(f"")
        lines.append(f"            // {comment}")
        for n in nodes:
            vname = visit_name(n["name"])
            pad = max(1, 44 - len(n["name"]) - 5)
            lines.append(f"            case {n['name']} _n:{' ' * pad}Visit{vname}(_n); break;")

    emit_cases(top_level,     "Top-level")
    emit_cases(members,       "Members")
    emit_cases(stmts_core,    "Statements — CORE")
    emit_cases(stmts_feature, "Statements — FEATURE")
    emit_cases(exprs_core,    "Expressions — CORE")
    emit_cases(exprs_feature, "Expressions — FEATURE")

    lines.append("")
    lines.append("            default:")
    lines.append("                VisitUnknown(node);")
    lines.append("                break;")
    lines.append("        }")
    lines.append("    }")
    lines.append("")
    lines.append("    // Called for node types not covered above — future-proofing; override to throw")
    lines.append("    // if strict exhaustion is required.")
    lines.append("    protected virtual void VisitUnknown(BoundNode node) { }")
    lines.append("")

    # Emit virtual protected methods
    def emit_section(nodes: list[dict], section_comment: str) -> None:
        lines.append(f"    // ---- {section_comment} ----")
        for n in nodes:
            vname = visit_name(n["name"])
            body_lines = emit_visitor_void_body(n)
            if not body_lines:
                lines.append(f"    protected virtual void Visit{vname}({n['name']} node) {{ }}")
            else:
                lines.append(f"    protected virtual void Visit{vname}({n['name']} node)")
                lines.append(f"    {{")
                for bl in body_lines:
                    # dedent 8 spaces to 4 for method body
                    lines.append(bl.replace("        ", "    ", 1))
                lines.append(f"    }}")
            lines.append("")

    # Special top-level
    lines.append("    // ---- Top-level ----")
    lines.append("    protected virtual void VisitCompilationUnit(BoundCompilationUnit node)")
    lines.append("    {")
    lines.append("        foreach (var m in node.Members)")
    lines.append("            VisitMember(m);")
    lines.append("    }")
    lines.append("")
    lines.append("    protected virtual void VisitMember(BoundMember member) => VisitNode(member);")
    lines.append("")

    # Members (hand-curated bodies — the schema-driven body matches what was hand-written)
    lines.append("    // ---- Members ----")
    for n in members:
        vname = visit_name(n["name"])
        body_lines = emit_visitor_void_body(n)
        if not body_lines:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node) {{ }}")
        else:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node)")
            lines.append(f"    {{")
            for bl in body_lines:
                lines.append(bl.replace("        ", "    ", 1))
            lines.append(f"    }}")
        lines.append("")

    # Statements
    lines.append("    // ---- Statements ----")
    lines.append("    protected virtual void VisitStatement(BoundStatement stmt) => VisitNode(stmt);")
    lines.append("")
    lines.append("    // CORE")
    for n in stmts_core:
        vname = visit_name(n["name"])
        body_lines = emit_visitor_void_body(n)
        if not body_lines:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node) {{ }}")
        else:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node)")
            lines.append(f"    {{")
            for bl in body_lines:
                lines.append(bl.replace("        ", "    ", 1))
            lines.append(f"    }}")
        lines.append("")

    lines.append("    // FEATURE")
    for n in stmts_feature:
        vname = visit_name(n["name"])
        body_lines = emit_visitor_void_body(n)
        if not body_lines:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node) {{ }}")
        else:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node)")
            lines.append(f"    {{")
            for bl in body_lines:
                lines.append(bl.replace("        ", "    ", 1))
            lines.append(f"    }}")
        lines.append("")

    # Expressions
    lines.append("    // ---- Expressions ----")
    lines.append("    protected virtual void VisitExpression(BoundExpression expr) => VisitNode(expr);")
    lines.append("")
    lines.append("    // CORE")
    for n in exprs_core:
        vname = visit_name(n["name"])
        body_lines = emit_visitor_void_body(n)
        if not body_lines:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node) {{ }}")
        else:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node)")
            lines.append(f"    {{")
            for bl in body_lines:
                lines.append(bl.replace("        ", "    ", 1))
            lines.append(f"    }}")
        lines.append("")

    lines.append("    // FEATURE")
    for n in exprs_feature:
        vname = visit_name(n["name"])
        body_lines = emit_visitor_void_body(n)
        if not body_lines:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node) {{ }}")
        else:
            lines.append(f"    protected virtual void Visit{vname}({n['name']} node)")
            lines.append(f"    {{")
            for bl in body_lines:
                lines.append(bl.replace("        ", "    ", 1))
            lines.append(f"    }}")
        lines.append("")

    lines.append("}")
    lines.append("")

    # ---- Generic typed visitor ----
    lines.append("// ---------------------------------------------------------------------------")
    lines.append("// Generic typed visitor (fold / accumulate)")
    lines.append("// ---------------------------------------------------------------------------")
    lines.append("")
    lines.append("public abstract class BoundTreeVisitor<T>")
    lines.append("{")
    lines.append("    public virtual T VisitNode(BoundNode node) => node switch")
    lines.append("    {")

    def emit_typed_cases(nodes: list[dict], comment: str) -> None:
        lines.append(f"        // {comment}")
        for n in nodes:
            vname = visit_name(n["name"])
            pad = max(1, 44 - len(n["name"]) - 4)
            lines.append(f"        {n['name']} _n{' ' * pad}=> Visit{vname}(_n),")

    emit_typed_cases(top_level,     "Top-level")
    emit_typed_cases(members,       "Members")
    emit_typed_cases(stmts_core,    "Statements — CORE")
    emit_typed_cases(stmts_feature, "Statements — FEATURE")
    emit_typed_cases(exprs_core,    "Expressions — CORE")
    emit_typed_cases(exprs_feature, "Expressions — FEATURE")

    lines.append("")
    lines.append("        _ => VisitUnknown(node),")
    lines.append("    };")
    lines.append("")
    lines.append("    protected abstract T DefaultResult { get; }")
    lines.append("    protected virtual T VisitUnknown(BoundNode node) => DefaultResult;")
    lines.append("")

    for group_comment, group_nodes in [
        ("Top-level",          top_level),
        ("Members",            members),
        ("Statements — CORE",  stmts_core),
        ("Statements — FEATURE", stmts_feature),
        ("Expressions — CORE", exprs_core),
        ("Expressions — FEATURE", exprs_feature),
    ]:
        lines.append(f"    // {group_comment}")
        for n in group_nodes:
            vname = visit_name(n["name"])
            lines.append(f"    protected virtual T Visit{vname}({n['name']} node) => DefaultResult;")
        lines.append("")

    lines.append("}")
    lines.append("// BoundNode is defined in BoundNodes.cs as the shared abstract class base.")
    lines.append("")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Generate BoundTreeRewriter.cs
# ---------------------------------------------------------------------------

REWRITER_HEADER = """\
// ============================================================================
// BoundTreeRewriter — default identity rewriter.
//
// GENERATED by bound-nodes-gen.py from bound-nodes.schema.json.
// DO NOT EDIT by hand — edit the schema and re-run the generator.
//
// A BoundTree → BoundTree transform pass derives from this class and overrides
// only the nodes it needs to rewrite. The default for every node is to recurse
// into children and, if ANYTHING changed, return a `with`-clone of the parent;
// if nothing changed, return the same reference (no allocation).
//
// That "changed?" check is strict reference equality (ReferenceEquals), which
// is correct because BoundNode subtypes override Equals to reference-based
// identity (preventing structural-record cycle through BoundType.Symbol).
//
// Lowering passes MUST derive from this rather than writing ad-hoc walks.
// The contract: every derived class must call base.Rewrite* for any child it
// does not explicitly replace — omitting a child corrupts the tree.
//
// FEATURE nodes: a post-lowering assertion pass (BoundFeatureAssert, in the
// Lowering library) derives from this and overrides every FEATURE Visit to
// throw InvalidOperationException("FEATURE node <T> reached CodeGen").
// ============================================================================

namespace Esharp.BoundTree;

public abstract class BoundTreeRewriter
{
"""


def ref_eq(fields: list[dict]) -> str:
    """Build ReferenceEquals chain for the given update fields."""
    parts = []
    for f in fields:
        t = f["type"]
        fname = f["name"]
        # value types / enums use ==
        if t in ("bool", "int", "string", "ConversionKind", "bool"):
            parts.append(f"{fname} == {fname}")
        elif t.startswith("SyntaxTokenKind") or "Kind" in t or "Role" in t or "Shape" in t or "Classification" in t:
            parts.append(f"{fname} == {fname}")
        else:
            parts.append(f"ReferenceEquals({fname}, {fname})")
    return " && ".join(parts)


def gen_rewriter(schema: dict) -> str:
    lines = [REWRITER_HEADER]

    all_stmt_nodes = [n for n in schema["nodes"]
                      if "name" in n and "fields" in n and n.get("category") == "Statement"]
    all_expr_nodes = [n for n in schema["nodes"]
                      if "name" in n and "fields" in n and n.get("category") == "Expression"]

    stmts_core    = [n for n in all_stmt_nodes if n["tier"] == "CORE"]
    stmts_feature = [n for n in all_stmt_nodes if n["tier"] == "FEATURE"]
    exprs_core    = [n for n in all_expr_nodes if n["tier"] == "CORE"]
    exprs_feature = [n for n in all_expr_nodes if n["tier"] == "FEATURE"]

    # RewriteStatement switch
    lines.append("    // ---- Entry points ----")
    lines.append("")
    lines.append("    public virtual BoundStatement RewriteStatement(BoundStatement stmt) => stmt switch")
    lines.append("    {")
    lines.append("        // CORE")
    for n in stmts_core:
        vname = n["name"][len("Bound"):]
        has_children = any(f.get("child_kind") for f in n.get("fields", [])) or n.get("force_rewrite", False)
        if not has_children:
            lines.append(f"        {n['name']} _n               => _n,")
        else:
            pad = max(1, 40 - len(n["name"]) - 4)
            lines.append(f"        {n['name']} _n{' ' * pad}=> Rewrite{vname}(_n),")
    lines.append("")
    lines.append("        // FEATURE")
    for n in stmts_feature:
        vname = n["name"][len("Bound"):]
        pad = max(1, 40 - len(n["name"]) - 4)
        lines.append(f"        {n['name']} _n{' ' * pad}=> Rewrite{vname}(_n),")
    lines.append("")
    lines.append("        _ => stmt,")
    lines.append("    };")
    lines.append("")

    # RewriteExpression switch
    lines.append("    public virtual BoundExpression RewriteExpression(BoundExpression expr) => expr switch")
    lines.append("    {")
    lines.append("        // CORE")
    for n in exprs_core:
        vname = n["name"][len("Bound"):]
        has_children = any(f.get("child_kind") for f in n.get("fields", [])) or n.get("force_rewrite", False)
        if not has_children:
            lines.append(f"        {n['name']} _n               => _n,")
        else:
            pad = max(1, 40 - len(n["name"]) - 4)
            lines.append(f"        {n['name']} _n{' ' * pad}=> Rewrite{vname}(_n),")
    lines.append("")
    lines.append("        // FEATURE")
    for n in exprs_feature:
        vname = n["name"][len("Bound"):]
        pad = max(1, 40 - len(n["name"]) - 4)
        lines.append(f"        {n['name']} _n{' ' * pad}=> Rewrite{vname}(_n),")
    lines.append("")
    lines.append("        _ => expr,")
    lines.append("    };")
    lines.append("")

    # Helpers
    lines += [
        "    // ---- Helpers ----",
        "",
        "    protected IReadOnlyList<BoundStatement> RewriteStatements(IReadOnlyList<BoundStatement> list)",
        "    {",
        "        List<BoundStatement>? result = null;",
        "        for (int i = 0; i < list.Count; i++)",
        "        {",
        "            var old = list[i];",
        "            var @new = RewriteStatement(old);",
        "            if (!ReferenceEquals(old, @new))",
        "            {",
        "                result ??= [.. list.Take(i)];",
        "            }",
        "            result?.Add(@new);",
        "        }",
        "        return result is null ? list : result;",
        "    }",
        "",
        "    protected IReadOnlyList<BoundExpression> RewriteExpressions(IReadOnlyList<BoundExpression> list)",
        "    {",
        "        List<BoundExpression>? result = null;",
        "        for (int i = 0; i < list.Count; i++)",
        "        {",
        "            var old = list[i];",
        "            var @new = RewriteExpression(old);",
        "            if (!ReferenceEquals(old, @new))",
        "            {",
        "                result ??= [.. list.Take(i)];",
        "            }",
        "            result?.Add(@new);",
        "        }",
        "        return result is null ? list : result;",
        "    }",
        "",
        "    protected IReadOnlyList<BoundFieldInit> RewriteFieldInits(IReadOnlyList<BoundFieldInit> list)",
        "    {",
        "        List<BoundFieldInit>? result = null;",
        "        for (int i = 0; i < list.Count; i++)",
        "        {",
        "            var old = list[i];",
        "            var @new = RewriteExpression(old.Value);",
        "            if (!ReferenceEquals(old.Value, @new))",
        "            {",
        "                result ??= [.. list.Take(i)];",
        "            }",
        "            result?.Add(result is null ? old : old with { Value = @new });",
        "        }",
        "        return result is null ? list : result;",
        "    }",
        "",
        "    protected BoundBlockStatement RewriteBlock(BoundBlockStatement block)",
        "    {",
        "        var stmts = RewriteStatements(block.Statements);",
        "        return ReferenceEquals(stmts, block.Statements) ? block : block with { Statements = stmts };",
        "    }",
        "",
    ]

    # Per-node rewrite methods for nodes that have child fields
    def emit_rewrite_node(n: dict, ret_type: str) -> None:
        vname = n["name"][len("Bound"):]
        fields = n.get("fields", [])
        child_fields = [(f["name"], f["child_kind"], f.get("nullable", False))
                        for f in fields if f.get("child_kind")]
        update_fields = [f for f in fields if f.get("update")]

        lines.append(f"    protected virtual {ret_type} Rewrite{vname}({n['name']} node)")
        lines.append(f"    {{")

        # special composite child kinds — emit their own full bodies
        special_kinds = {"catch_list", "match_arm_list", "match_expr_arm_list",
                         "select_arm_list", "interp_part_list", "if_expr_branch_list"}

        has_special = any(ck in special_kinds for _, ck, _ in child_fields)

        if has_special:
            # Delegate to the hand-crafted body — emit a pass-through call comment
            # Actually the generator must produce the full body, same as the hand-written version.
            ck_name = next(ck for _, ck, _ in child_fields if ck in special_kinds)
            _emit_special_rewrite(n, lines, child_fields, update_fields)
        else:
            # Standard: rewrite each child, compare, clone if changed
            for fname, ck, nullable in child_fields:
                if ck == "expr":
                    if nullable:
                        lines.append(f"        var {param_name(fname)} = node.{fname} is {{}} __{fname} ? RewriteExpression(__{fname}) : null;")
                    else:
                        lines.append(f"        var {param_name(fname)} = RewriteExpression(node.{fname});")
                elif ck == "call_expr":
                    lines.append(f"        var {param_name(fname)} = (BoundCallExpression)RewriteExpression(node.{fname});")
                elif ck == "stmt":
                    if nullable:
                        lines.append(f"        var {param_name(fname)} = node.{fname} is {{}} __{fname} ? RewriteStatement(__{fname}) : null;")
                    else:
                        lines.append(f"        var {param_name(fname)} = RewriteStatement(node.{fname});")
                elif ck == "block":
                    lines.append(f"        var {param_name(fname)} = RewriteBlock(node.{fname});")
                elif ck == "expr_list":
                    lines.append(f"        var {param_name(fname)} = RewriteExpressions(node.{fname});")
                elif ck == "stmt_list":
                    lines.append(f"        var {param_name(fname)} = RewriteStatements(node.{fname});")
                elif ck == "field_init_list":
                    lines.append(f"        var {param_name(fname)} = RewriteFieldInits(node.{fname});")
                elif ck == "member":
                    # member lists are not rewritten in the base (they contain declarations, not expressions)
                    # Skip — member rewriting is declaration-level, done by pillar 2 binder passes
                    pass

            # Build same check
            same_parts = []
            for fname, ck, nullable in child_fields:
                if ck == "member":
                    continue
                lname = param_name(fname)
                same_parts.append(f"ReferenceEquals({lname}, node.{fname})")
            if same_parts:
                if len(same_parts) == 1:
                    lines.append(f"        if ({same_parts[0]}) return node;")
                else:
                    lines.append(f"        bool _same = {same_parts[0]}")
                    for i, p in enumerate(same_parts[1:], start=1):
                        trail = ";" if i == len(same_parts) - 1 else ""
                        lines.append(f"                  && {p}{trail}")
                    lines.append(f"        if (_same) return node;")
                # with-clone using update fields (child fields that have update=true)
                with_parts = []
                for fname, ck, nullable in child_fields:
                    if ck == "member":
                        continue
                    lname = param_name(fname)
                    with_parts.append(f"{fname} = {lname}")
                lines.append(f"        return node with {{ {', '.join(with_parts)} }};")
            else:
                lines.append(f"        return node;")

        lines.append(f"    }}")
        lines.append("")

    def _emit_special_rewrite(n: dict, out: list[str], child_fields, update_fields) -> None:
        """Emit the full body for nodes with composite child_kinds."""
        node_name = n["name"]
        vname = node_name[len("Bound"):]
        special_kinds = {"catch_list", "match_arm_list", "match_expr_arm_list",
                         "select_arm_list", "interp_part_list", "if_expr_branch_list"}

        # Find the special kind
        the_ck = next((ck for _, ck, _ in child_fields if ck in special_kinds), None)

        if the_ck == "catch_list":
            out += [
                "        var body = RewriteBlock(node.Body);",
                "        List<BoundCatchClause>? catches = null;",
                "        for (int i = 0; i < node.Catches.Count; i++)",
                "        {",
                "            var c = node.Catches[i];",
                "            var newBody  = RewriteBlock(c.Body);",
                "            var newGuard = c.Guard is { } g ? RewriteExpression(g) : null;",
                "            bool catchSame = ReferenceEquals(newBody, c.Body) && ReferenceEquals(newGuard, c.Guard);",
                "            if (!catchSame)",
                "            {",
                "                catches ??= [.. node.Catches.Take(i)];",
                "                catches.Add(c with { Body = newBody, Guard = newGuard });",
                "            }",
                "            else catches?.Add(c);",
                "        }",
                "        IReadOnlyList<BoundCatchClause> finalCatches = catches ?? node.Catches;",
                "        bool same = ReferenceEquals(body, node.Body) && catches is null;",
                "        return same ? node : node with { Body = body, Catches = finalCatches };",
            ]
        elif the_ck == "match_arm_list":
            out += [
                "        var subject = RewriteExpression(node.Subject);",
                "        List<BoundMatchArm>? arms = null;",
                "        for (int i = 0; i < node.Arms.Count; i++)",
                "        {",
                "            var arm = node.Arms[i];",
                "            var newGuard = arm.Guard is { } g ? RewriteExpression(g) : null;",
                "            var newBody  = RewriteBlock(arm.Body);",
                "            bool armSame = ReferenceEquals(newGuard, arm.Guard) && ReferenceEquals(newBody, arm.Body);",
                "            if (!armSame)",
                "            {",
                "                arms ??= [.. node.Arms.Take(i)];",
                "                arms.Add(arm with { Guard = newGuard, Body = newBody });",
                "            }",
                "            else arms?.Add(arm);",
                "        }",
                "        bool same = ReferenceEquals(subject, node.Subject) && arms is null;",
                "        return same ? node : node with { Subject = subject, Arms = arms ?? node.Arms };",
            ]
        elif the_ck == "match_expr_arm_list":
            out += [
                "        var subject = RewriteExpression(node.Subject);",
                "        List<BoundMatchExpressionArm>? arms = null;",
                "        for (int i = 0; i < node.Arms.Count; i++)",
                "        {",
                "            var arm = node.Arms[i];",
                "            var newGuard = arm.Guard is { } g ? RewriteExpression(g) : null;",
                "            var newVal   = RewriteExpression(arm.Value);",
                "            bool armSame = ReferenceEquals(newGuard, arm.Guard) && ReferenceEquals(newVal, arm.Value);",
                "            if (!armSame)",
                "            {",
                "                arms ??= [.. node.Arms.Take(i)];",
                "                arms.Add(arm with { Guard = newGuard, Value = newVal });",
                "            }",
                "            else arms?.Add(arm);",
                "        }",
                "        bool same = ReferenceEquals(subject, node.Subject) && arms is null;",
                "        return same ? node : node with { Subject = subject, Arms = arms ?? node.Arms };",
            ]
        elif the_ck == "select_arm_list":
            out += [
                "        List<BoundSelectArm>? arms = null;",
                "        for (int i = 0; i < node.Arms.Count; i++)",
                "        {",
                "            var arm  = node.Arms[i];",
                "            var ch   = arm.Channel is { } c ? RewriteExpression(c) : null;",
                "            var val  = arm.Value   is { } v ? RewriteExpression(v) : null;",
                "            var body = RewriteBlock(arm.Body);",
                "            bool same = ReferenceEquals(ch, arm.Channel) && ReferenceEquals(val, arm.Value) && ReferenceEquals(body, arm.Body);",
                "            if (!same)",
                "            {",
                "                arms ??= [.. node.Arms.Take(i)];",
                "                arms.Add(arm with { Channel = ch, Value = val, Body = body });",
                "            }",
                "            else arms?.Add(arm);",
                "        }",
                "        return arms is null ? node : node with { Arms = arms };",
            ]
        elif the_ck == "interp_part_list":
            out += [
                "        List<BoundInterpolationPart>? parts = null;",
                "        for (int i = 0; i < node.Parts.Count; i++)",
                "        {",
                "            var p = node.Parts[i];",
                "            if (p.Expr is null) { parts?.Add(p); continue; }",
                "            var newExpr = RewriteExpression(p.Expr);",
                "            if (!ReferenceEquals(newExpr, p.Expr))",
                "            {",
                "                parts ??= [.. node.Parts.Take(i)];",
                "                parts.Add(p with { Expr = newExpr });",
                "            }",
                "            else parts?.Add(p);",
                "        }",
                "        return parts is null ? node : node with { Parts = parts };",
            ]
        elif the_ck == "if_expr_branch_list":
            out += [
                "        List<BoundIfExpressionBranch>? branches = null;",
                "        for (int i = 0; i < node.Branches.Count; i++)",
                "        {",
                "            var b     = node.Branches[i];",
                "            var cond  = RewriteExpression(b.Condition);",
                "            var stmts = RewriteStatements(b.Body);",
                "            var val   = b.Value is { } v ? RewriteExpression(v) : null;",
                "            bool same = ReferenceEquals(cond, b.Condition)",
                "                     && ReferenceEquals(stmts, b.Body)",
                "                     && ReferenceEquals(val, b.Value);",
                "            if (!same)",
                "            {",
                "                branches ??= [.. node.Branches.Take(i)];",
                "                branches.Add(b with { Condition = cond, Body = stmts, Value = val });",
                "            }",
                "            else branches?.Add(b);",
                "        }",
                "        var elseStmts = RewriteStatements(node.ElseBody);",
                "        var elseVal   = node.ElseValue is { } ev ? RewriteExpression(ev) : null;",
                "        bool nodeSame = branches is null",
                "                     && ReferenceEquals(elseStmts, node.ElseBody)",
                "                     && ReferenceEquals(elseVal,   node.ElseValue);",
                "        return nodeSame",
                "            ? node",
                "            : node with { Branches = branches ?? node.Branches, ElseBody = elseStmts, ElseValue = elseVal };",
            ]

    lines.append("    // ---- CORE statements ----")
    lines.append("")
    for n in stmts_core:
        child_fields = [(f["name"], f["child_kind"], f.get("nullable", False))
                        for f in n.get("fields", []) if f.get("child_kind")]
        if child_fields or n.get("force_rewrite", False):
            emit_rewrite_node(n, "BoundStatement")

    lines.append("    // ---- FEATURE statements ----")
    lines.append("")
    for n in stmts_feature:
        child_fields = [(f["name"], f["child_kind"], f.get("nullable", False))
                        for f in n.get("fields", []) if f.get("child_kind")]
        if child_fields or n.get("force_rewrite", False):
            emit_rewrite_node(n, "BoundStatement")

    lines.append("    // ---- CORE expressions ----")
    lines.append("")
    for n in exprs_core:
        child_fields = [(f["name"], f["child_kind"], f.get("nullable", False))
                        for f in n.get("fields", []) if f.get("child_kind")]
        if child_fields or n.get("force_rewrite", False):
            emit_rewrite_node(n, "BoundExpression")

    lines.append("    // ---- FEATURE expressions ----")
    lines.append("")
    for n in exprs_feature:
        child_fields = [(f["name"], f["child_kind"], f.get("nullable", False))
                        for f in n.get("fields", []) if f.get("child_kind")]
        if child_fields or n.get("force_rewrite", False):
            emit_rewrite_node(n, "BoundExpression")

    lines.append("}")
    lines.append("")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Generate BoundNodes.Update.cs
# ---------------------------------------------------------------------------

UPDATE_HEADER = """\
// ============================================================================
// BoundNodes.Update — Update factory methods for every sealed BoundNode record.
//
// GENERATED by bound-nodes-gen.py from bound-nodes.schema.json.
// DO NOT EDIT by hand — edit the schema and re-run the generator.
//
// Update is the correct way for a rewriter to produce a modified node when the
// signature of the node type is unknown to the caller (i.e., in generated code).
// It mirrors Roslyn's BoundNodes.xml-generated `Update(...)` methods: each
// overload takes exactly the update-flagged constructor parameters; if ALL values
// are reference-equal (or value-equal for scalars) to the originals it returns
// `this`; otherwise it returns a `with`-clone with the new values.
//
// Rules:
//  - `BoundType` parameters are compared with ReferenceEquals (structural equality
//    on BoundType recurses through TypeSymbol.BoundView, causing stack overflow).
//  - Lists are compared by reference. Same-contents new list → new node.
//  - Span is NOT a parameter — it propagates from the original node unchanged.
// ============================================================================

using Esharp.Syntax;
using Esharp.Symbols;

namespace Esharp.BoundTree;

"""

def _is_ref_type(t: str) -> bool:
    """Return True if the type should use ReferenceEquals rather than ==."""
    scalars = {"bool", "int", "string", "ConversionKind", "InheritanceRole",
               "AsyncReturnShape", "DataClassification", "SelectArmKind"}
    bare = t.split(".")[-1].strip("?")
    return bare not in scalars and not bare.startswith("Syntax.") and "Kind" not in bare


def gen_update(schema: dict) -> str:
    lines = [UPDATE_HEADER]

    all_nodes = [n for n in schema["nodes"]
                 if "name" in n and "fields" in n
                 and n.get("base") in ("BoundNode", "BoundMember", "BoundStatement", "BoundExpression")]

    for n in all_nodes:
        update_fields = [f for f in n.get("fields", []) if f.get("update")]
        if not update_fields:
            continue

        node_name = n["name"]
        # Comment header
        tier = n.get("tier", "")
        cat  = n.get("category", "")
        if cat == "Member":
            lines.append(f"// ---- {cat} ----")
        elif cat == "Statement" and tier == "CORE" and update_fields:
            pass  # grouped below

        lines.append(f"public partial record {node_name}")
        lines.append(f"{{")

        # Build parameter list
        params = []
        for f in update_fields:
            t = f["type"]
            nullable = f.get("nullable", False)
            suffix = "?" if nullable and not t.endswith("?") else ""
            # lowercase first char for param name, escaping C# keywords (Else -> @else)
            pname = param_name(f["name"])
            params.append((t + suffix, pname, f["name"]))

        # Method signature
        if len(params) == 1:
            t, pname, fname = params[0]
            lines.append(f"    public {node_name} Update({t} {pname})")
        else:
            lines.append(f"    public {node_name} Update(")
            for i, (t, pname, fname) in enumerate(params):
                comma = "," if i < len(params) - 1 else ""
                lines.append(f"        {t} {pname}{comma}")
            lines.append(f"    )")

        lines.append(f"    {{")

        # Guard
        guard_parts = []
        for t, pname, fname in params:
            bare_t = t.rstrip("?")
            if _is_ref_type(bare_t):
                guard_parts.append(f"ReferenceEquals({pname}, {fname})")
            else:
                guard_parts.append(f"{pname} == {fname}")

        if len(guard_parts) == 1:
            lines.append(f"        if ({guard_parts[0]}) return this;")
        else:
            lines.append(f"        if ({guard_parts[0]}")
            for i, gp in enumerate(guard_parts[1:], 1):
                trail = ")" if i == len(guard_parts) - 1 else ""
                lines.append(f"         && {gp}{trail}")
            lines.append(f"            return this;")

        # Clone
        with_assigns = ", ".join(f"{fname} = {pname}" for _, pname, fname in params)
        lines.append(f"        return this with {{ {with_assigns} }};")
        lines.append(f"    }}")
        lines.append(f"}}")
        lines.append("")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> None:
    schema = load_schema()

    # Validate: every node entry with "name" must have either no fields (supporting)
    # or a "base" and a "category".
    for n in schema["nodes"]:
        if "name" not in n:
            continue  # comment-only entry
        if n.get("category") == "Supporting":
            continue
        if "fields" not in n:
            print(f"WARNING: node {n['name']} has no 'fields' key", file=sys.stderr)

    visitor_src  = gen_visitor(schema)
    rewriter_src = gen_rewriter(schema)
    update_src   = gen_update(schema)

    (OUT_DIR / "BoundTreeVisitor.cs").write_text(visitor_src, encoding="utf-8")
    (OUT_DIR / "BoundTreeRewriter.cs").write_text(rewriter_src, encoding="utf-8")
    (OUT_DIR / "BoundNodes.Update.cs").write_text(update_src, encoding="utf-8")

    print(f"Generated BoundTreeVisitor.cs, BoundTreeRewriter.cs, BoundNodes.Update.cs")
    print(f"  schema: {SCHEMA_PATH}")
    print(f"  output: {OUT_DIR}")


if __name__ == "__main__":
    main()
