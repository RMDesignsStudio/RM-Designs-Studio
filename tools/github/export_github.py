"""Run: py -3 "tools/github/export_github.py"   (from the RM Designs Studio folder)

Builds the files of the RM-Designs-Studio GitHub repository (github.com/RMDesignsStudio/RM-Designs-Studio) in
GitHub/RM-Designs-Studio, beside the apps. It is a clean copy, made the way Team Simplified's simplified-suite is laid
out (one repository, one folder per app, shared libraries beside them); the working folders are never a repository.

What goes in: shared (Studio Core, Studio UI, EgoEngineLibrary), Swapper, Helmet Swapper, and the steering wheel engine
(the Steering Wheel Swapper's WheelSwap.Core, as "Wheel Swap Engine"; the Steering Wheel Swapper app itself is not
published), plus the repository's own files from tools/github/root. Left out: build output, dist / share / test
output, parked work (reference), branding sources. Personal test defaults are blanked, and the copy is checked for
personal paths before anything is written.

Only the files are made here; committing and pushing is done separately, when the owner asks.
"""
import pathlib
import re
import shutil
import sys

studio = pathlib.Path(__file__).resolve().parents[2]
repo = studio / "GitHub" / "RM-Designs-Studio"
sws = studio / "Steering Wheel Swapper"

PARTS = [
    ("shared", studio / "shared"),
    ("Swapper", studio / "Swapper"),
    ("Helmet Swapper", studio / "Helmet Swapper"),
    ("Wheel Swap Engine/WheelSwap.Core", sws / "src" / "WheelSwap.Core"),
]
SKIP_DIRS = {"bin", "obj", ".vs", ".git", "dist", "dist-next", "share", "out", "reference", "branding",
             "_removed_from_egoengine", "TestResults"}
SKIP_SUFFIXES = {".user", ".suo", ".serps", ".simplified", ".erp"}
# local-only tools (they package share zips on the owner's PC and name its folders)
SKIP_PATHS = {"Swapper/tools/make_share.py"}
TEXT = {".cs", ".xaml", ".csproj", ".props", ".slnx", ".json", ".md", ".py", ".ps1", ".txt", ".svg", ".html", ".xml",
        ".resx", ".gitignore", ".js", ".css", ""}

# repository-only adjustments: (path in the repo, regex, replacement, expected matches)
REWRITES = [
    # the wheel engine sits in its own folder in the repository
    ("Swapper/Swapper.slnx", r"\.\./Steering Wheel Swapper/src/WheelSwap\.Core/", "../Wheel Swap Engine/WheelSwap.Core/", 1),
    ("Swapper/Directory.Build.props", r"'\.\.', 'Steering Wheel Swapper', 'src', 'WheelSwap\.Core'", "'..', 'Wheel Swap Engine', 'WheelSwap.Core'", 1),
    ("Swapper/Directory.Build.props", r"<!-- the steering wheel engine:[^>]*-->",
     "<!-- the steering wheel engine (WheelSwap.Core), in its own folder beside this app -->", 1),
    # the owner's own test locations: the tests skip without them, or take the SWAPPER_* variables
    ("Swapper/tests/Swapper.Tests/Env.cs", r'(Var\("SWAPPER_[A-Z]+", )@"[^"]*"', r'\1""', 3),
    ("Swapper/tests/gui_smoke.ps1", r'\[string\]\$Template = "[^"]*"', '[string]$Template = $(throw "pass -Template <file.serps>")', 1),
]
# this PC's own names: the Windows user folder and where the Studio lives (read here, never written into the repo)
PERSONAL = re.compile("|".join(re.escape(s) for s in {pathlib.Path.home().name, str(studio.parent), str(studio.parent).replace("\\", "/"),
                                                         str(studio.parent).replace("\\", "\\\\")}).encode(), re.I)
DRIVE_PATH = re.compile(rb'["\'@][A-Z]:\\\\?[A-Za-z]')   # a literal drive path in the source: listed for a look, not blocked


def wanted(rel: pathlib.Path) -> bool:
    return not any(p in SKIP_DIRS for p in rel.parts[:-1]) and rel.suffix.lower() not in SKIP_SUFFIXES


def main() -> None:
    files: dict[str, bytes] = {}
    for name, root in PARTS:
        for p in sorted(root.rglob("*")):
            if p.is_file() and wanted(rel := p.relative_to(root)) and f"{name}/{rel.as_posix()}" not in SKIP_PATHS:
                files[f"{name}/{rel.as_posix()}"] = p.read_bytes()
    here = pathlib.Path(__file__).resolve().parent
    for p in sorted((here / "root").rglob("*")):
        if p.is_file():
            files[p.relative_to(here / "root").as_posix()] = p.read_bytes()
    files["LICENSE"] = (studio / "Swapper" / "LICENSE").read_bytes()
    files["Wheel Swap Engine/Directory.Packages.props"] = (sws / "Directory.Packages.props").read_bytes()
    files["Wheel Swap Engine/global.json"] = (sws / "global.json").read_bytes()
    # how the repository is made, so it can be made again
    files["tools/github/export_github.py"] = pathlib.Path(__file__).read_bytes()
    for p in sorted((here / "root").rglob("*")):
        if p.is_file():
            files["tools/github/root/" + p.relative_to(here / "root").as_posix()] = p.read_bytes()

    for path, pattern, new, expected in REWRITES:
        raw = files[path]
        bom = raw.startswith(b"\xef\xbb\xbf")
        text, n = re.subn(pattern, new, raw.decode("utf-8-sig"))
        if n != expected:
            sys.exit(f"stopped, nothing written: {path} matched {n}x (expected {expected}): {pattern}")
        files[path] = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")

    textual = {p: d for p, d in files.items() if pathlib.PurePosixPath(p).suffix.lower() in TEXT}
    leaks = [p for p, d in textual.items() if PERSONAL.search(d)]
    if leaks:
        sys.exit("stopped, nothing written: personal paths in " + ", ".join(leaks))
    for p, d in textual.items():
        for m in DRIVE_PATH.finditer(d):
            line = d[:m.start()].count(b"\n") + 1
            print(f"  note: drive path in {p}:{line}: {d.splitlines()[line - 1].strip()[:110].decode('utf-8', 'replace')}")

    # write: the repository folder is refreshed completely (its .git history is kept)
    repo.mkdir(parents=True, exist_ok=True)
    for child in repo.iterdir():
        if child.name == ".git":
            continue
        shutil.rmtree(child) if child.is_dir() else child.unlink()
    for path, data in files.items():
        out = repo / path
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(data)
    size = sum(len(d) for d in files.values())
    print(f"{len(files)} files, {size / 1e6:.1f} MB -> {repo}")
    for name, _ in PARTS:
        print(f"  {name}: {sum(1 for p in files if p.startswith(name + '/'))} files")


if __name__ == "__main__":
    main()
