[Setup]
AppName=Audio Sample Splitter
AppVersion=1.0.0
DefaultDirName={pf}\Audio Sample Splitter
DefaultGroupName=Audio Sample Splitter
OutputDir=.
OutputBaseFilename=AudioSplitterSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs

[Icons]
Name: "{group}\Audio Sample Splitter"; Filename: "{app}\AudioSplitter.exe"

[Run]
Filename: "{app}\AudioSplitter.exe"; Description: "Launch the application"; Flags: nowait postinstall skipifsilent
