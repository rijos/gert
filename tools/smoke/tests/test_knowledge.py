"""test_knowledge.py — upload → status pills → use-in-chat (§9).

Drives the knowledge panel: upload a small text file through the hidden file
input, watch the status pill go Processing → Ready (the client polls
``GET .../documents/{id}``), and toggle use-in-chat.
"""

from __future__ import annotations

import re

from playwright.sync_api import FilePayload, Page, expect

from tools.smoke.pages import AppPage


def _open(page: Page, base_url: str) -> AppPage:
    app = AppPage(page)
    app.goto(base_url, "/")
    app.wait_ready()
    return app


def test_upload_shows_status_pill_and_reaches_ready(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.knowledge.open()  # reveal the knowledge view (doc list lives there)
    # Upload via the hidden composer file input (the SPA routes uploads through it).
    payload: FilePayload = {
        "name": "notes.txt",
        "mimeType": "text/plain",
        "buffer": b"sqlite-vec keeps the homelab stack to a single file.",
    }
    app.knowledge.file_input.set_input_files(files=[payload])
    doc = app.knowledge.doc("notes.txt")
    expect(doc).to_be_visible(timeout=15000)
    # The pill transitions to ready once ingestion (real adapter → embeddings mock) finishes.
    expect(app.knowledge.doc_pill("notes.txt")).to_have_class(
        # ready pill carries the "ready" kind class
        re.compile(r"ready"),
        timeout=30000,
    )


def test_use_in_chat_toggle(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.knowledge.open()  # the use-in-chat switch lives in the knowledge view
    switch = app.knowledge.use_in_chat_switch
    expect(switch).to_be_visible()
    switch.click()
    # Toggling flips the composer "Use my docs" button on too (shared state).
    expect(app.composer.use_docs_toggle).to_have_class(re.compile(r"\bon\b"))
