#ifndef PayloadPreflightList
  #error Supply the generated inventory through Build-Release.ps1.
#endif

function FirstUnavailablePayloadFile(const Directory: String): String;
var Files: TStringList; Index: Integer; Path: String; Stream: TFileStream;
begin
  Result := '';
  if not DirExists(Directory) then Exit;
  Files := TStringList.Create;
  try
    { The exact same inventory generates [Files]. Do not enumerate destination
      EXEs: Setup itself, old uninstallers and user files are not payload. }
#include PayloadPreflightList
    for Index := 0 to Files.Count - 1 do begin
      Path := AddBackslash(Directory) + Files[Index];
      if DirExists(Path) then begin
        Log('Payload preflight blocked: destination is a directory: ' + Path);
        Result := Path;
        Exit;
      end;
      if FileExists(Path) then begin
        try
          { Open existing bytes exclusively; never write or truncate. }
          Stream := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
          Stream.Free;
        except
          Log('Payload preflight blocked: ' + Path + ': ' + GetExceptionMessage);
          Result := Path;
          Exit;
        end;
      end;
    end;
  finally Files.Free; end;
end;
