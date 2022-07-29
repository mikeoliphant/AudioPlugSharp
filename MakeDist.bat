del /S /Q Dist
mkdir Dist
mkdir Dist\x64
echo * > Dist\.gitignore
echo !.gitignore >> Dist\.gitignore
copy x64\Release\*.dll Dist\x64
copy x64\Release\*.json Dist\x64
copy x64\Release\*.vst3 Dist\x64
copy AudioPlugSharpWPF\bin\Release\net6.0-windows\AudioPlugSharpWPF.dll Dist\x64
