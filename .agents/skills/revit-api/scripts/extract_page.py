"""
extract_page.py - 从 RevitAPI 预提取的 JSON 数据中获取格式化 Markdown

用法:
    python extract_page.py --type "Autodesk.Revit.DB.Wall"          # 通过类型全名
    python extract_page.py --id "T:Autodesk.Revit.DB.Wall"          # 通过 Help ID
    python extract_page.py --id "P:Autodesk.Revit.DB.Wall.Flipped"  # 成员级别
"""

import argparse
import difflib
import json
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
SKILL_DIR = SCRIPT_DIR.parent
INDEX_PATH = SKILL_DIR / "data" / "api_index.json"
PAGES_DIR = SKILL_DIR / "data" / "pages"
LOOKUP_PATH = PAGES_DIR / "_lookup.json"


def load_lookup():
    """加载 help_id → namespace 查找表"""
    if not LOOKUP_PATH.exists():
        return {}
    with open(LOOKUP_PATH, "r", encoding="utf-8") as f:
        return json.load(f)


_PAGES_CACHE: dict[str, dict] = {}


def load_namespace_pages(ns: str) -> dict:
    """加载命名空间下的所有页面（带缓存）"""
    if ns in _PAGES_CACHE:
        return _PAGES_CACHE[ns]

    safe_name = ns.replace("::", ".") if ns else "_orphan"
    ns_path = PAGES_DIR / f"{safe_name}.json"
    if not ns_path.exists():
        _PAGES_CACHE[ns] = {}
        return _PAGES_CACHE[ns]

    with open(ns_path, "r", encoding="utf-8") as f:
        _PAGES_CACHE[ns] = json.load(f)

    return _PAGES_CACHE[ns]


def load_page(ns: str, help_id: str) -> dict | None:
    """从命名空间 JSON 文件加载单个页面"""
    pages = load_namespace_pages(ns)
    return pages.get(help_id)


def normalize_help_id(raw_help_id: str) -> str:
    """规范化 Help ID 输入"""
    help_id = raw_help_id.strip()

    # 用户可能直接传类型全名
    if ":" not in help_id and "." in help_id and not help_id.startswith("Overload:"):
        return f"T:{help_id}"

    return help_id


def infer_namespace_from_help_id(help_id: str) -> str | None:
    """在 lookup 缺失时，从 Help ID 推断命名空间"""
    if not help_id:
        return None

    if help_id.startswith("N:"):
        return help_id[2:]

    if help_id.startswith("Overload:"):
        body = help_id[len("Overload:"):]
    elif ":" in help_id:
        _, body = help_id.split(":", 1)
    else:
        body = help_id

    body = body.split("(", 1)[0]

    # 成员级 ID：先去掉成员名，得到类型全名
    if body.count(".") >= 2 and not help_id.startswith("T:"):
        type_fqn = body.rsplit(".", 1)[0]
    else:
        type_fqn = body

    if "." not in type_fqn:
        return None

    return type_fqn.rsplit(".", 1)[0]


def list_ns_candidates(ns: str, target: str, limit: int = 8) -> list[str]:
    """在同命名空间内给出候选 Help ID"""
    pages = load_namespace_pages(ns)
    if not pages:
        return []

    keys = list(pages.keys())
    # 先做包含匹配，再做相似匹配
    contains = [k for k in keys if target.lower() in k.lower()]
    if contains:
        return contains[:limit]

    return difflib.get_close_matches(target, keys, n=limit, cutoff=0.45)


def resolve_overload_help_id(help_id: str, ns: str) -> tuple[str | None, list[str]]:
    """解析 Overload: 前缀，返回命中的具体方法 ID"""
    if not help_id.startswith("Overload:"):
        return None, []

    root = help_id[len("Overload:"):]
    root = root[2:] if root.startswith("M:") else root
    pages = load_namespace_pages(ns)
    if not pages:
        return None, []

    matches = [k for k in pages.keys() if k.startswith(f"M:{root}(")]
    matches.sort()
    if not matches:
        return None, []

    # 返回首个可渲染页面，同时把全部重载都返回给调用方提示
    return matches[0], matches


def format_page(data: dict) -> str:
    """将 JSON 页面数据格式化为 Markdown"""
    lines = []

    # 标题
    lines.append(f"# {data.get('title', 'Unknown')}")
    lines.append("")

    # 描述
    desc = data.get("desc", "")
    if desc:
        lines.append(f"> {desc}")
        lines.append("")

    # 命名空间
    ns = data.get("ns", "")
    if ns:
        lines.append(f"**Namespace**: `{ns}`")
        lines.append(f"**Assembly**: RevitAPI.dll")
        lines.append("")

    # C# 签名
    sig = data.get("sig", "")
    if sig:
        lines.append("## Syntax")
        lines.append("")
        lines.append("```csharp")
        lines.append(sig)
        lines.append("```")
        lines.append("")

    # 继承层次
    inherit = data.get("inherit", [])
    if inherit:
        lines.append("## Inheritance Hierarchy")
        lines.append("")
        for i, item in enumerate(inherit):
            indent = "  " * i
            lines.append(f"{indent}- {item}")
        lines.append("")

    # 成员表
    member_labels = {
        "props": "Properties",
        "methods": "Methods",
        "events": "Events",
        "fields": "Fields",
    }
    members = data.get("members", {})
    for key, label in member_labels.items():
        items = members.get(key, [])
        if not items:
            continue
        lines.append(f"## {label}")
        lines.append("")
        lines.append("| Name | Description |")
        lines.append("|------|-------------|")
        for m in items:
            name = m.get("n", "")
            mdesc = m.get("d", "").replace("\n", " ")
            lines.append(f"| `{name}` | {mdesc} |")
        lines.append("")

    # Remarks
    remarks = data.get("remarks", "")
    if remarks:
        lines.append("## Remarks")
        lines.append("")
        lines.append(remarks)
        lines.append("")

    return "\n".join(lines)


def resolve_type_to_help_id(type_name: str) -> str | None:
    """通过 api_index.json 将类型名转为 Help ID"""
    if not INDEX_PATH.exists():
        return None
    with open(INDEX_PATH, "r", encoding="utf-8") as f:
        index = json.load(f)

    # 精确匹配
    if type_name in index["types"]:
        return f"T:{type_name}"

    # 不区分大小写
    tn_lower = type_name.lower()
    for fqn, info in index["types"].items():
        if fqn.lower() == tn_lower:
            return f"T:{fqn}"
        if info["name"].lower() == tn_lower:
            return f"T:{fqn}"

    # 尝试直接在 lookup 中查找各种前缀
    lookup = load_lookup()
    for prefix in ("T:", "N:", "P:", "M:", "E:", "F:"):
        candidate = f"{prefix}{type_name}"
        if candidate in lookup:
            return candidate

    return None


def resolve_help_id(help_id: str, lookup: dict[str, str]) -> tuple[str | None, str | None, list[str], str | None]:
    """
    解析 Help ID，返回:
    - resolved_help_id
    - namespace
    - overload_candidates
    - warning_message
    """
    normalized = normalize_help_id(help_id)

    # Overload 优先展开到具体方法签名
    if normalized.startswith("Overload:"):
        ns = infer_namespace_from_help_id(normalized)
        if ns:
            resolved_overload, overloads = resolve_overload_help_id(normalized, ns)
            if resolved_overload:
                warning = (
                    f"提示: 检测到 Overload，已自动解析到 `{resolved_overload}`；"
                    f"共 {len(overloads)} 个重载。"
                )
                return resolved_overload, ns, overloads, warning

            pages = load_namespace_pages(ns)
            if normalized in pages:
                warning = "提示: 未找到具体重载，已回退到 Overload 总览页。"
                return normalized, ns, [], warning

    # 1) 直接命中 lookup
    ns = lookup.get(normalized)
    if ns:
        return normalized, ns, [], None

    # 2) lookup 缺失时，尝试从 ID 推断命名空间并在页面文件中直查
    ns = infer_namespace_from_help_id(normalized)
    if ns:
        pages = load_namespace_pages(ns)
        if normalized in pages:
            return normalized, ns, [], None

        # 3) 支持 Overload: 前缀（兼容无 lookup 的场景）
        resolved_overload, overloads = resolve_overload_help_id(normalized, ns)
        if resolved_overload:
            warning = (
                f"提示: 检测到 Overload，已自动解析到 `{resolved_overload}`；"
                f"共 {len(overloads)} 个重载。"
            )
            return resolved_overload, ns, overloads, warning

    return None, ns, [], None


def main():
    parser = argparse.ArgumentParser(description="从预提取 JSON 获取 API 文档")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--type", type=str, help="类型全名 (如 Autodesk.Revit.DB.Wall)")
    group.add_argument("--id", type=str, help="Help ID (如 T:Autodesk.Revit.DB.Wall)")
    args = parser.parse_args()

    lookup = load_lookup()

    if args.type:
        help_id = resolve_type_to_help_id(args.type)
        if not help_id:
            print(f"错误: 未找到类型 \"{args.type}\"")
            sys.exit(1)
    else:
        help_id = normalize_help_id(args.id)

    resolved_help_id, ns, overload_candidates, warning = resolve_help_id(help_id, lookup)
    if not resolved_help_id or not ns:
        print(f"错误: Help ID \"{help_id}\" 未找到")
        print("可用前缀: T:(类型) P:(属性) M:(方法) E:(事件) F:(字段) N:(命名空间) Overload:(重载)")

        inferred_ns = infer_namespace_from_help_id(help_id)
        if inferred_ns:
            candidates = list_ns_candidates(inferred_ns, help_id)
            if candidates:
                print("\n可能的候选 ID:")
                for item in candidates[:8]:
                    print(f"  - {item}")
        sys.exit(1)

    data = load_page(ns, resolved_help_id)
    if not data:
        print(f"错误: 未找到页面数据 (ns={ns}, id={resolved_help_id})")
        sys.exit(1)

    if warning:
        print(warning)
        print()

    if overload_candidates:
        print("## 可用重载")
        print()
        for item in overload_candidates:
            print(f"- `{item}`")
        print()

    print(format_page(data))


if __name__ == "__main__":
    main()
