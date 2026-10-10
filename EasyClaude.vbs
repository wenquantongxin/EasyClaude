Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
root = fso.GetParentFolderName(WScript.ScriptFullName)
exe = root & "\app\EasyClaude.exe"

If Not fso.FileExists(exe) Then
    MsgBox "找不到 app\EasyClaude.exe，请确认本脚本位于 Easy Claude 根目录。", 48, "Easy Claude"
    WScript.Quit 1
End If

On Error Resume Next
Set lnk = sh.CreateShortcut(root & "\Easy Claude.lnk")
lnk.TargetPath = sh.ExpandEnvironmentStrings("%SystemRoot%") & "\System32\wscript.exe"
lnk.Arguments = """" & root & "\EasyClaude.vbs"""
lnk.WorkingDirectory = root
If fso.FileExists(root & "\EasyClaude.ico") Then lnk.IconLocation = root & "\EasyClaude.ico" Else lnk.IconLocation = exe & ",0"
lnk.Save
On Error GoTo 0

sh.CurrentDirectory = root
sh.Run """" & exe & """", 1, False
