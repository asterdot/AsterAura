# Third-Party Licenses

This document contains the full text of licenses for third-party software incorporated into or referenced by Aster Aura.

---

## 1. dsh-aurachat

**Repository**: https://github.com/hsushjk/dsh-aurachat  
**Copyright**: (c) hsushjk  
**License**: MIT License

```
MIT License

Copyright (c) hsushjk

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Components Used**:
- 业务逻辑蓝本（记忆/心情/缺口/日记/自驱/表情/语音/guard）
- 已重写为 C# 实现

---

## 2. aurachat-client-win

**Repository**: https://github.com/hsushjk/aurachat-client-win  
**Copyright**: (c) hsushjk  
**License**: MIT License

```
MIT License

Copyright (c) hsushjk

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Components Used**:
- 桌面端形态参考
- WPF + WebView2 架构参考

---

## 3. aurachat-client-android

**Repository**: https://github.com/hsushjk/aurachat-client-android  
**Copyright**: (c) hsushjk  
**License**: MIT License

```
MIT License

Copyright (c) hsushjk

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Components Used**:
- 移动端连接模型参考
- 协议兼容性设计

---

## 4. Inspiration and Design References (Non-Code)

The following projects provided inspiration and design concepts, but no code was directly incorporated:

### dsh-roost (tdcq9594)
- **License**: Custom Non-Commercial License (Prohibits commercial use)
- **Usage**: Design concepts only (pluggable persona, AI in IM)
- **Note**: No code copied due to license restrictions

### digital-life-core (mashangshui)
- **License**: No LICENSE file (all rights reserved)
- **Usage**: Concepts only (file-based memory, stateless invocation)
- **Note**: No code copied due to lack of license

---

## NuGet Package Dependencies

Third-party libraries used in Aster Aura (installed via NuGet):

### .NET Runtime and ASP.NET Core
- **License**: MIT License
- **Copyright**: .NET Foundation and Contributors
- **Website**: https://dot.net

### WebView2 Runtime
- **License**: Microsoft Software License Terms
- **Copyright**: Microsoft Corporation
- **Website**: https://developer.microsoft.com/microsoft-edge/webview2/

### xUnit (Testing Framework)
- **License**: Apache License 2.0
- **Copyright**: .NET Foundation
- **Website**: https://xunit.net/

### Additional Dependencies

For a complete list of NuGet package dependencies and their licenses, see the project's `.csproj` files or run:

```bash
dotnet list package --include-transitive
```

---

## Compliance Notes

1. **MIT License Obligations**: All MIT-licensed upstream code attributions are preserved in this document and in the main LICENSE file.

2. **Design References**: Projects used only for design inspiration (dsh-roost, digital-life-core) are acknowledged but no code was incorporated due to license restrictions.

3. **Commercial Use**: This project (Aster Aura) is MIT-licensed and can be used commercially. The upstream MIT projects also permit commercial use.

4. **Attribution**: When distributing Aster Aura, include this file along with the main LICENSE to satisfy upstream attribution requirements.

---

Last Updated: 2026-11-08
