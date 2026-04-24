import "./child.mod" as child;

comptime fn from_root() -> u32 {
    return child.answer();
}
