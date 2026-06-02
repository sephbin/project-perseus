"""
query_dump_gui.py
Tkinter GUI for exploring Perseus full-sync dumps written by FullSyncRunner to
%AppData%\\ProjectPerseus\\fullsync-dumps\\<timestamp>-<docname>-fullsync.json.

Stdlib only — tkinter ships with Python on Windows, no pip installs required.

Usage:
    python tools/query_dump_gui.py
    python tools/query_dump_gui.py path/to/dump.json   # open at launch

Layout:
    - Top bar:    file path + Open / Summary buttons
    - Left:       search controls (Name regex / Unique ID / Element ID / Parameter)
                  and results table
    - Right:      selected element info + its parameters
    - Bottom:     status bar (counts, errors)

Results display is capped at 5000 rows to keep ttk.Treeview responsive on big
dumps; the status bar shows "N of M" when truncation kicks in.
"""

import json
import re
import sys
import tkinter as tk
from collections import Counter
from pathlib import Path
from tkinter import filedialog, ttk

MAX_DISPLAY = 5000


class DumpExplorer(tk.Tk):
    def __init__(self, initial_path=None):
        super().__init__()
        self.title("Perseus Dump Explorer")
        self.geometry("1200x720")

        self.data = None
        self.elements = []
        self.deleted = []
        self._all_inner = []
        self._current_results = []

        self._build_ui()
        if initial_path:
            self._load_file(initial_path)

    def _build_ui(self):
        top = ttk.Frame(self)
        top.pack(side="top", fill="x", padx=8, pady=6)
        ttk.Label(top, text="File:").pack(side="left")
        self.path_var = tk.StringVar()
        ttk.Entry(top, textvariable=self.path_var, state="readonly").pack(
            side="left", fill="x", expand=True, padx=(4, 4)
        )
        ttk.Button(top, text="Open...", command=self._open_file).pack(side="left")
        ttk.Button(top, text="Summary", command=self._show_summary).pack(
            side="left", padx=(4, 0)
        )

        main = ttk.PanedWindow(self, orient="horizontal")
        main.pack(side="top", fill="both", expand=True, padx=8, pady=(0, 6))

        left = ttk.Frame(main)
        main.add(left, weight=1)

        search_box = ttk.LabelFrame(left, text="Search")
        search_box.pack(side="top", fill="x", padx=4, pady=4)

        ttk.Label(search_box, text="Mode:").grid(row=0, column=0, sticky="w", padx=4, pady=2)
        self.mode_var = tk.StringVar(value="Name (regex)")
        modes = ["Name (regex)", "Unique ID", "Element ID", "Parameter"]
        ttk.Combobox(
            search_box, textvariable=self.mode_var, values=modes,
            state="readonly", width=18,
        ).grid(row=0, column=1, sticky="w", padx=4, pady=2)

        ttk.Label(search_box, text="Query:").grid(row=1, column=0, sticky="w", padx=4, pady=2)
        self.query_var = tk.StringVar()
        q_entry = ttk.Entry(search_box, textvariable=self.query_var, width=40)
        q_entry.grid(row=1, column=1, sticky="we", padx=4, pady=2)
        q_entry.bind("<Return>", lambda _e: self._do_search())

        ttk.Label(search_box, text="Value (param mode):").grid(
            row=2, column=0, sticky="w", padx=4, pady=2
        )
        self.value_var = tk.StringVar()
        v_entry = ttk.Entry(search_box, textvariable=self.value_var, width=40)
        v_entry.grid(row=2, column=1, sticky="we", padx=4, pady=2)
        v_entry.bind("<Return>", lambda _e: self._do_search())

        ttk.Button(search_box, text="Search", command=self._do_search).grid(
            row=3, column=0, columnspan=2, sticky="e", padx=4, pady=4
        )
        search_box.columnconfigure(1, weight=1)

        results_box = ttk.LabelFrame(left, text="Results")
        results_box.pack(side="top", fill="both", expand=True, padx=4, pady=4)
        # "index" is the element's position in the outer JSON "elements" list.
        # Django's add_to_crud_queue chunks at 1000 elements per background task,
        # so chunk = index // 1000 (0-indexed) for ghost-hunting / debugging.
        cols = ("index", "element_id", "name", "category")
        self.results = ttk.Treeview(results_box, columns=cols, show="headings")
        for c, w in zip(cols, (70, 110, 240, 140)):
            self.results.heading(c, text=c)
            self.results.column(c, width=w, anchor="w")
        self.results.pack(side="left", fill="both", expand=True)
        sb = ttk.Scrollbar(results_box, orient="vertical", command=self.results.yview)
        sb.pack(side="right", fill="y")
        self.results.configure(yscrollcommand=sb.set)
        self.results.bind("<<TreeviewSelect>>", self._show_selected)

        right = ttk.Frame(main)
        main.add(right, weight=2)

        info = ttk.LabelFrame(right, text="Element")
        info.pack(side="top", fill="x", padx=4, pady=4)
        self.info_text = tk.Text(info, height=7, wrap="word")
        self.info_text.pack(fill="both", expand=True, padx=4, pady=4)
        self.info_text.configure(state="disabled")

        params_box = ttk.LabelFrame(right, text="Parameters")
        params_box.pack(side="top", fill="both", expand=True, padx=4, pady=4)
        pcols = ("name", "value", "value_type", "param_id_type")
        self.params = ttk.Treeview(params_box, columns=pcols, show="headings")
        for c, w in zip(pcols, (240, 260, 140, 110)):
            self.params.heading(c, text=c)
            self.params.column(c, width=w, anchor="w")
        self.params.pack(side="left", fill="both", expand=True)
        psb = ttk.Scrollbar(params_box, orient="vertical", command=self.params.yview)
        psb.pack(side="right", fill="y")
        self.params.configure(yscrollcommand=psb.set)

        self.status_var = tk.StringVar(value="No file loaded.")
        ttk.Label(self, textvariable=self.status_var, anchor="w", relief="sunken").pack(
            side="bottom", fill="x"
        )

    def _open_file(self):
        path = filedialog.askopenfilename(
            title="Open Perseus dump",
            filetypes=[("JSON", "*.json"), ("All files", "*.*")],
        )
        if path:
            self._load_file(path)

    def _load_file(self, path):
        p = Path(path)
        self.path_var.set(str(p))
        self.status_var.set(f"Loading {p.name} ({p.stat().st_size / 1_048_576:.1f} MB)...")
        self.update_idletasks()
        try:
            self.data = json.loads(p.read_text(encoding="utf-8"))
        except Exception as exc:
            self.status_var.set(f"Load failed: {exc}")
            return
        self.elements = self.data.get("elements", [])
        self.deleted = self.data.get("deletedElements", [])
        # (index_in_outer_list, inner_element_dict) — index is preserved through
        # filtering so the results table can show it and the user can map back to
        # the upload chunk.
        self._all_inner = [
            (i, d["element"]) for i, d in enumerate(self.elements) if "element" in d
        ]
        self.status_var.set(
            f"{len(self._all_inner)} elements, {len(self.deleted)} deletions loaded."
        )
        self._populate_results(self._all_inner)

    def _do_search(self):
        if not self.data:
            self.status_var.set("Load a file first.")
            return
        mode = self.mode_var.get()
        q = self.query_var.get().strip()
        v = self.value_var.get().strip()
        results = []
        try:
            if mode == "Name (regex)":
                rx = re.compile(q or ".*", re.IGNORECASE)
                results = [(i, e) for (i, e) in self._all_inner if rx.search(e.get("name") or "")]
            elif mode == "Unique ID":
                results = [(i, e) for (i, e) in self._all_inner if e.get("unique_id") == q]
            elif mode == "Element ID":
                results = [(i, e) for (i, e) in self._all_inner if str(e.get("element_id")) == q]
            elif mode == "Parameter":
                for i, e in self._all_inner:
                    for p in e.get("parameters", []):
                        if p.get("name") != q:
                            continue
                        if v == "" or str(p.get("value")) == v:
                            results.append((i, e))
                            break
        except re.error as exc:
            self.status_var.set(f"Bad regex: {exc}")
            return
        self._populate_results(results)

    def _populate_results(self, results):
        self.results.delete(*self.results.get_children())
        shown = results[:MAX_DISPLAY]
        for row, (idx, e) in enumerate(shown):
            cat = ""
            for p in e.get("parameters", []):
                if p.get("name") == "Category":
                    cat = p.get("value") or ""
                    break
            self.results.insert(
                "", "end",
                iid=str(row),
                values=(idx, e.get("element_id"), e.get("name"), cat),
            )
        self._current_results = shown
        total = len(results)
        if total > MAX_DISPLAY:
            self.status_var.set(f"{total} matches (showing first {MAX_DISPLAY}).")
        else:
            self.status_var.set(f"{total} matches.")

    def _show_selected(self, _event=None):
        sel = self.results.selection()
        if not sel:
            return
        idx, elem = self._current_results[int(sel[0])]
        info = (
            f"json index:     {idx}   (chunk {idx // 1000} @ 1000/chunk)\n"
            f"element_id:     {elem.get('element_id')}\n"
            f"unique_id:      {elem.get('unique_id')}\n"
            f"name:           {elem.get('name')}\n"
            f"last_edited_by: {elem.get('last_edited_by')}\n"
            f"source_model:   {elem.get('source_model')}\n"
            f"source_state:   {elem.get('source_state')}\n"
        )
        self.info_text.configure(state="normal")
        self.info_text.delete("1.0", "end")
        self.info_text.insert("end", info)
        self.info_text.configure(state="disabled")
        self.params.delete(*self.params.get_children())
        for p in elem.get("parameters", []):
            self.params.insert(
                "", "end",
                values=(
                    p.get("name"),
                    p.get("value"),
                    p.get("value_type"),
                    p.get("param_id_type"),
                ),
            )

    def _show_summary(self):
        if not self.data:
            self.status_var.set("Load a file first.")
            return
        cats = Counter()
        for _, e in self._all_inner:
            for p in e.get("parameters", []):
                if p.get("name") == "Category":
                    cats[p.get("value")] += 1
                    break
        top = "\n".join(f"  {n:>6}  {c}" for c, n in cats.most_common(15))
        msg = (
            f"Elements:     {len(self._all_inner)}\n"
            f"Deletions:    {len(self.deleted)}\n"
            f"Source GUID:  {self.data.get('documentGuid')}\n"
            f"State:        {self.data.get('source_state')}\n"
            f"User/Machine: {self.data.get('windowsUser')} / {self.data.get('machine')}\n"
            f"Timestamp:    {self.data.get('timestamp')}\n\n"
            f"Top categories:\n{top}"
        )
        win = tk.Toplevel(self)
        win.title("Summary")
        win.geometry("540x460")
        text = tk.Text(win, wrap="word")
        text.pack(fill="both", expand=True, padx=8, pady=8)
        text.insert("end", msg)
        text.configure(state="disabled")


if __name__ == "__main__":
    initial = sys.argv[1] if len(sys.argv) > 1 else None
    DumpExplorer(initial).mainloop()
