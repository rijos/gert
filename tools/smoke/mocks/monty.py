"""monty.py — mock of the monty sandbox sidecar (testing.md §4.2).

The real ``Gert.External`` ``MontySandbox`` adapter (the default ``ISandbox`` backend)
POSTs ``/run`` here in the FakeE2E profile, so the adapter HTTP path (typed client,
request shaping, response mapping, graceful failure) is exercised against a wire-level
fake — no ``pydantic-monty``, no Rust.

Deterministic by design: a tiny whitelisted-AST evaluator handles the ``print(<expr>)``
calculator shape that ``run_python`` is built for (the ``fixtures.json`` sandbox case is
``print(2 + 2)`` → ``4``); anything outside that subset returns a monty-style error on
stderr with a non-zero exit, exactly as the real interpreter would for an unsupported
construct, so the tool loop's failure path is exercised too.
"""

from __future__ import annotations

import ast
from typing import Any

from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route


def _eval(node: ast.AST) -> int | float:
    """Evaluate a whitelisted arithmetic node — numbers, +-*/%** and unary +/-.

    No names, calls, attributes, or comprehensions: the evaluator can only do maths,
    so feeding it the model's code can never have a side effect (it is a stand-in for
    monty's sandbox, not a second one).
    """
    if isinstance(node, ast.Expression):
        return _eval(node.body)
    if isinstance(node, ast.Constant):
        if isinstance(node.value, bool) or not isinstance(node.value, (int, float)):
            raise ValueError("only numeric literals are supported")
        return node.value
    if isinstance(node, ast.UnaryOp):
        operand = _eval(node.operand)
        if isinstance(node.op, ast.UAdd):
            return +operand
        if isinstance(node.op, ast.USub):
            return -operand
        raise ValueError("unsupported unary operator")
    if isinstance(node, ast.BinOp):
        left, right = _eval(node.left), _eval(node.right)
        if isinstance(node.op, ast.Add):
            return left + right
        if isinstance(node.op, ast.Sub):
            return left - right
        if isinstance(node.op, ast.Mult):
            return left * right
        if isinstance(node.op, ast.Div):
            return left / right
        if isinstance(node.op, ast.Mod):
            return left % right
        if isinstance(node.op, ast.Pow):
            return left**right
        raise ValueError("unsupported operator")
    raise ValueError("unsupported expression")


def _print_argument(code: str) -> str | None:
    """Return the single argument source of a lone ``print(...)`` statement, else None."""
    try:
        tree = ast.parse(code.strip(), mode="exec")
    except SyntaxError:
        return None
    if len(tree.body) != 1:
        return None
    stmt = tree.body[0]
    if not isinstance(stmt, ast.Expr):
        return None
    call = stmt.value
    if (
        not isinstance(call, ast.Call)
        or not isinstance(call.func, ast.Name)
        or call.func.id != "print"
        or len(call.args) != 1
        or call.keywords
    ):
        return None
    return ast.unparse(call.args[0])


def _run_code(code: str) -> tuple[str, str, int]:
    """Return (stdout, stderr, exit_code) for the ``print(<arithmetic>)`` shape."""
    inner = _print_argument(code)
    if inner is None:
        return (
            "",
            "MontyRuntimeError: the mock sandbox only supports print(<arithmetic>)",
            1,
        )
    try:
        value = _eval(ast.parse(inner, mode="eval"))
    except (ValueError, SyntaxError, ArithmeticError) as exc:
        return "", f"MontyRuntimeError: {exc}", 1
    rendered = int(value) if isinstance(value, float) and value.is_integer() else value
    return f"{rendered}\n", "", 0


async def run(request: Request) -> JSONResponse:
    payload: dict[str, Any] = await request.json()
    raw_code = payload.get("code", "")
    code = raw_code if isinstance(raw_code, str) else ""
    cap = int(payload.get("max_output_bytes", 64 * 1024))

    stdout, stderr, exit_code = _run_code(code)
    return JSONResponse(
        {
            "stdout": stdout[:cap],
            "stderr": stderr[:cap],
            "exit_code": exit_code,
            "timed_out": False,
        }
    )


def create_app() -> Starlette:
    return Starlette(routes=[Route("/run", run, methods=["POST"])])


app = create_app()
