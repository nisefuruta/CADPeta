"""compare_renders.py はパッケージ化していない単体スクリプトなので、
テストから `import compare_renders` できるよう親ディレクトリを sys.path へ足す。"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
