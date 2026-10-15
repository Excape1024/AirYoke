# AirYoke

AirYoke 将安卓手机变成微软模拟飞行的有线体感操纵器。手机负责采集姿态和触控输入，Windows 桥接程序通过 SimConnect 把操纵量发送给模拟器。

![AirYoke 手机界面](docs/images/手机界面.png)

当前版本为 **1.4**，主要面向 **Microsoft Flight Simulator 2020 默认 Asobo A320neo**。

## 功能

- 手机倾斜控制副翼和升降舵，支持一键校准及轴向反转。
- 大尺寸油门杆，带反推、慢车、CL、FLX 和 TOGA 卡位。
- 油门手动接管与交还，交还时发送 CL 目标后停止覆盖飞机油门。
- 方向舵松手自动回中。
- 连续减速板、减速板预位、五档襟翼、双轮刹车和驻车刹车。
- 回传空速、高度、自动驾驶、自动油门、襟翼及减速板状态。
- 优先使用 USB 网络共享的 UDP 通道；不可用时自动回退到 `adb reverse` TCP 通道。
- 500 毫秒断线保护，自动释放反推、刹车和三个姿态操纵轴。
- 内置协议与控制输出自检，以及不会驱动飞机的空转诊断模式。

## 使用要求

- Windows 10 或 Windows 11，64 位。
- Microsoft Flight Simulator 2020。
- Android 13 或更高版本。
- 一根支持数据传输的 USB 线。
- 推荐机型：默认 Asobo A320neo。

## 快速开始

1. 打开 [Releases](https://github.com/Excape1024/AirYoke/releases)，下载 `AirYoke-v1.4.apk` 和 `AirYoke-v1.4-Windows-x64.zip`。
2. 在手机上安装 APK。若系统阻止安装，请为当前文件管理器临时允许“安装未知应用”。
3. 解压 Windows 压缩包，使用 USB 线连接手机和电脑。
4. 在手机上开启“开发者选项”和“USB 调试”，首次连接时允许这台电脑调试。
5. 运行 `AirYoke.Bridge.exe`，再在手机上打开 AirYoke。
6. 保持手机处于自然水平姿态，点击“校准”。状态显示“已连接”后即可操作。

完整的安装、操作与排障步骤见 [详细使用教程](docs/使用教程.md)。需要自行编译时请阅读 [开发指南](docs/开发指南.md)。

## 连接方式

AirYoke 会自动选择可用通道，无需填写电脑 IP：

1. **USB 网络共享 + UDP**：推荐，适合约 100 Hz 的连续操纵数据。
2. **USB 调试 + adb reverse + TCP**：自动回退方案，发布包已带所需 ADB 文件。

两种方式均使用端口 `9000`。程序不需要通过互联网传输飞行控制数据。

## 兼容性与当前限制

- 手机端 1.4 与桥接端 1.4 使用协议四，必须配套使用；旧版本不能混用。
- CL/FLX 默认轴值为 89%/95%，可在手机设置中按机型调整。
- 当前发布 APK 使用调试签名，适合侧载测试，不适合作为应用商店正式签名包。
- 源码、协议、自检、真机链路和界面输入已经验证；MSFS2020 中默认 A320neo 的襟翼、减速板、油门和回读仍需更多真实飞行验证。
- 标准 SimConnect 已接入；MobiFlight 仅保留诊断探针，运行控制不依赖 MobiFlight。

## 安全提示

`--reversetest`、`--controltest` 和 `--axistest` 会实际操纵飞机，只能在地面和安全状态下使用。一般用户无需运行这些命令。

## 项目结构

```text
app/       Android 手机端
bridge/    Windows SimConnect 桥接程序
docs/      使用教程、开发说明和界面图片
gradle/    Gradle Wrapper
```

## 验证状态

- Android JVM 单元测试：73 项通过。
- Android Release 构建：通过。
- Android Lint：0 错误，存在 37 项非阻塞警告。
- Windows 桥接程序自检：通过。
- Samsung SM-S9380 真机安装、触摸输入、UDP 与 ADB 通信：通过。

详细范围与未验收项见 [验证说明](docs/验证说明.md)。

## 参与开发

欢迎通过 Issue 报告问题。请尽量附上手机型号、Android 版本、MSFS 版本、完整机型名称、使用的连接方式、桥接程序输出，以及可复现步骤。不要上传账号凭据或其他隐私信息。

