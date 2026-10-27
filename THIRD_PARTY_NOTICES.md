# Third-party notices

The application source is licensed under GPL-3.0-only. The complete license is in LICENSE.

The Windows executable includes Microsoft .NET and WPF 10.0.14. Their licenses and third-party notices are in docs/licenses and are embedded in the executable as assembly resources. Microsoft .NET and WPF use the MIT license; their dependencies retain their respective licenses.

The verified EC interface and fan-table layout are based on:

- minortex/mech-forza-control, GPL-3.0-only, revision 3a8df18b91a86b3a21951b8cd1d1418f86dec6ca: https://github.com/minortex/mech-forza-control
- w568w's WUJIE14XA interface research: https://gist.github.com/w568w/b2fc5f9d1f4dff13efe751abec27b396

This package does not include the OEM driver, control-center binaries, decompiled OEM source, authentication credentials, or the earlier Python application runtime. It uses the OEM driver that is already installed on the supported machine. Windows supplies the system fonts and icons.
