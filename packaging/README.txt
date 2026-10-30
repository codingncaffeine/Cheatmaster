Cheatmaster for Linux
=====================

Run ./cheatmaster from this folder (or "cheatmaster" once installed from a package).

Reading another program's memory
--------------------------------
Most distributions only let a program read the memory of programs it started itself
(the Yama "ptrace scope" setting is 1). Games started by Steam are not, so Cheatmaster
shows a red banner with an "Allow memory access" button. It asks for your password
once (through polkit) and sets kernel.yama.ptrace_scope to 0 until the next restart.
That lets programs of your own user read each other's memory; it never reaches other
users' programs.

To keep it permanently instead:
    echo 'kernel.yama.ptrace_scope = 0' | sudo tee /etc/sysctl.d/10-ptrace.conf

Wine and Proton games are fully supported: they are listed by their .exe name, and
their modules (game.exe, the DLLs) are found so saved addresses carry over from
Windows tables.

Global hotkeys
--------------
Hotkeys work on X11 sessions, and on Wayland while a game running through XWayland
has focus (every Wine/Proton game by default).

GitHub backup
-------------
The sign-in token is kept in your desktop keyring (GNOME Keyring, KWallet) through
secret-tool. Without a keyring it is stored in a file only your user can read.

Requirements
------------
x86-64 Linux with X11 or XWayland. The program is self-contained: no .NET install is
needed. The -musl build is for musl distributions such as Alpine.
