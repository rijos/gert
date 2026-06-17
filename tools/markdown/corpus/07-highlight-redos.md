```csharp
var s = @"verbatim ""escaped"" string";
string interp = $"value {x}";
```

```cpp
const char* raw = R"delim(raw (string) content)delim";
#include <vector>
auto x = 0xDEADBEEF;
```

```rust
let r = r#"raw "string" here"#;
let b = br##"bytes"##;
macro_rules! m { () => {}; }
```

```bash
echo "$HOME" ${var#prefix} $# $? # real comment
for f in *.txt; do echo "$f"; done
```

```json
{"key": "value", "n": -1.5e10, "ok": true, "nil": null}
```

```xml
<!DOCTYPE html><root attr="v"><!-- c --><![CDATA[x]]></root>
```
