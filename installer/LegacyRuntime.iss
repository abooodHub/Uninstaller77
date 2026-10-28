#include "LegacyRuntimeHashes.iss"

function IsKnownLegacyFile(const Directory, Name: String): Boolean;
var Path: String;
begin
  Path := AddBackslash(Directory) + Name;
  Result := False;
  if not FileExists(Path) then Exit;
  try Result := IsKnownLegacyHash(Name, GetSHA256OfFile(Path));
  except Log('Legacy identity could not be read; file kept: ' + Path); end;
end;

procedure AddKnownLegacyFiles(const Directory: String; Files: TStringList);
begin
  if IsKnownLegacyFile(Directory, 'WinUninstaller.exe') then Files.Add('WinUninstaller.exe');
  if IsKnownLegacyFile(Directory, 'WinUninstaller.dll') then Files.Add('WinUninstaller.dll');
  if IsKnownLegacyFile(Directory, 'WinUninstaller.deps.json') then Files.Add('WinUninstaller.deps.json');
  if IsKnownLegacyFile(Directory, 'WinUninstaller.runtimeconfig.json') then Files.Add('WinUninstaller.runtimeconfig.json');
end;

function FirstUnavailableLegacyRuntime(const Directory: String): String;
var Files: TStringList; Index: Integer; Path: String; Stream: TFileStream;
begin
  Result := '';
  Files := TStringList.Create;
  try
    AddKnownLegacyFiles(Directory, Files);
    for Index := 0 to Files.Count - 1 do begin
      Path := AddBackslash(Directory) + Files[Index];
      try
        Stream := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
        Stream.Free;
      except Result := Path; Exit; end;
    end;
  finally Files.Free; end;
end;

procedure RetireKnownLegacyRuntime(const Directory: String);
var Files: TStringList; Index: Integer; Path: String;
begin
  { Called only after all new payload hashes and version have been verified.
    Never enumerate DLLs, delete a directory, or remove user/settings data. }
  Files := TStringList.Create;
  try
    AddKnownLegacyFiles(Directory, Files);
    for Index := 0 to Files.Count - 1 do begin
      Path := AddBackslash(Directory) + Files[Index];
      if IsKnownLegacyFile(Directory, Files[Index]) then begin
        if not DeleteFile(Path) then RaiseException('The new application is installed, but an old verified runtime file could not be retired: ' + Path);
        Log('Retired verified legacy runtime artifact: ' + Path);
      end;
    end;
  finally Files.Free; end;
end;
