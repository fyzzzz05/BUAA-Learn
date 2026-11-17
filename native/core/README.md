# 跨平台业务内核（开发中）

Rust 内核供 Swift 和未来的 C# 应用调用。目前包含学校请求策略、课程与材料解析、文本覆盖率、录播自动化规划和执行逻辑、课堂分析及问答来源校验、时间轴、账号隔离记录、旧数据库只读导入和 C ABI。Mac 登录原型通过 Swift URLSession 传输接入学期、课程、课表、录播目录和课堂材料，学校同步曾使用真实账号验证；原型使用独立的本地数据库，并已接入应用运行期间的自动化调度。自动化的学校授权和模型调用目前以模拟服务验证，正式界面和完整迁移仍未完成。

```sh
cargo test --locked --manifest-path native/core/Cargo.toml
cargo build --release --locked --manifest-path native/core/Cargo.toml
```

构建同时输出 Rust library、静态库及 `cdylib`。Windows C# 原型通过 `buaa_core.dll` 调用；入口 `buaa_core_abi_version` 当前返回 major version 1，宿主应先验证版本再创建句柄。macOS 原型继续显式链接静态 archive，避免因增加 dylib 输出而引入额外运行时依赖。Windows 原型与当前验证范围见 [Windows README](../windows/README.md)。

`include/buaa_core.h` 中的 JSON 接口接收以 NUL 结尾的 UTF-8 输入，返回包含 `ok` 和 `data` 或 `error` 的 UTF-8 JSON；调用方必须通过 `buaa_core_string_free` 恰好释放一次。学校会话在内存中持有令牌，并通过临时请求回调发送；不保存密码或令牌。

旧数据导入方法在独立目标数据库上运行，仅以只读方式打开旧数据库，并在一个目标事务中复制记录和设置；目标不为空、路径指向源数据库或数据格式损坏时停止。调用方仍须先备份旧数据并处理导出文件。该功能仅用合成数据库测试，尚未对用户数据执行，尚不能作为完整迁移工具。Windows 下的文件同一性、访问控制和并发行为还需在真实系统上验证。

旧版账号标识为学校账号的 SHA-256 前 24 位，原生版使用学校账号本身；直接按原键复制不会把记录关联到当前原生账号。`preview_legacy_account` 只对对应账号进行只读预检，分别统计可新增、相同和冲突记录，不修改两侧数据。`merge_legacy` 只是保留原键的底层工具，遇到不同内容的同键记录时整体回滚，不能直接作为账号迁移流程。正式迁移仍需账号映射、冲突处理、备份及旧版导出文件处理。
