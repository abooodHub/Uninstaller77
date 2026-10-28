{ Test-only reproduction of the old destination scan. Never ship this function. }
function LegacyFirstUnavailablePayloadFile(const Directory: String): String;
var Find: TFindRec; Path, Extension: String; Stream: TFileStream;
begin
  Result := '';
  if not DirExists(Directory) then Exit;
  if FindFirst(AddBackslash(Directory) + '*', Find) then begin
    try
      repeat
        if (Find.Name <> '.') and (Find.Name <> '..') then begin
          Path := AddBackslash(Directory) + Find.Name;
          if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
            { Reparse points and runtime profile data are not installer payload. }
            if ((Find.Attributes and $400) = 0) and (CompareText(Find.Name, 'WebView2') <> 0) then
              Result := LegacyFirstUnavailablePayloadFile(Path);
          end else begin
            Extension := Lowercase(ExtractFileExt(Path));
            if (Extension = '.dll') or (Extension = '.exe') then begin
              try
                { Open existing bytes exclusively; never write or truncate. }
                Stream := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
                Stream.Free;
              except
                Log('Payload preflight blocked: ' + Path + ': ' + GetExceptionMessage);
                Result := Path;
              end;
            end;
          end;
          if Result <> '' then Break;
        end;
      until not FindNext(Find);
    finally FindClose(Find); end;
  end;
end;


