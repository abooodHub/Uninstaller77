const
  ProgressBackground = {#ThemeCanvas};
  ProgressCardColor = {#ThemeSurface};
  ProgressTrackColor = {#ThemeTrack};
  ProgressGreen = {#ThemeAccent};
  ProgressHighlight = {#ThemeAccentHover};
  ProgressScale = 1000;
  SPI_GETCLIENTAREAANIMATION = $1042;

var
  ProgressSurface, ProgressCard: TPanel;
  ProgressStage, ProgressPercent: TNewStaticText;
  ProgressBarImage: TBitmapImage;
  ProgressTimer: LongWord;
  TargetProgress, DrawnProgress, HighlightPosition: Integer;

function SetProgressTimer(Window: HWND; Id: LongWord; Interval: UINT; Callback: LongWord): LongWord;
  external 'SetTimer@user32.dll stdcall';
function KillProgressTimer(Window: HWND; Id: LongWord): Boolean;
  external 'KillTimer@user32.dll stdcall';
function ReadClientAnimations(Action, Param: UINT; var Enabled: LongWord; Flags: UINT): Boolean;
  external 'SystemParametersInfoW@user32.dll stdcall';

procedure DrawInstallProgress;
var
  Canvas: TCanvas;
  Bounds: TRect;
  BarWidth, BarHeight, Filled, FillRadius, HighlightStart, HighlightEnd: Integer;
begin
  BarWidth := ProgressBarImage.Width;
  BarHeight := ProgressBarImage.Height;
  Canvas := ProgressBarImage.Bitmap.Canvas;
  Canvas.Brush.Color := ProgressCardColor;
  Bounds.Left := 0;
  Bounds.Top := 0;
  Bounds.Right := BarWidth;
  Bounds.Bottom := BarHeight;
  Canvas.FillRect(Bounds);
  Canvas.Pen.Style := psClear;
  Canvas.Brush.Color := ProgressTrackColor;
  Canvas.RoundRect(0, 0, BarWidth, BarHeight, BarHeight, BarHeight);

  Filled := BarWidth * DrawnProgress div ProgressScale;
  if (DrawnProgress > 0) and (Filled = 0) then Filled := 1;
  if Filled > 0 then begin
    FillRadius := BarHeight;
    if Filled < FillRadius then FillRadius := Filled;
    Canvas.Brush.Color := ProgressGreen;
    Canvas.RoundRect(0, 0, Filled, BarHeight, FillRadius, BarHeight);

    { Sweep through the filled region, without covering the unfinished track. }
    if (ProgressTimer <> 0) and (Filled > 2 * BarHeight) then begin
      HighlightStart := HighlightPosition - ScaleX(38);
      HighlightEnd := HighlightPosition;
      if HighlightStart < BarHeight div 2 then HighlightStart := BarHeight div 2;
      if HighlightEnd > Filled - BarHeight div 2 then HighlightEnd := Filled - BarHeight div 2;
      if HighlightEnd > HighlightStart then begin
        Canvas.Brush.Color := ProgressHighlight;
        Canvas.RoundRect(HighlightStart, BarHeight div 4, HighlightEnd, 3 * BarHeight div 4, BarHeight, BarHeight);
      end;
    end;
  end;
  ProgressBarImage.Invalidate;
end;

procedure InstallProgressTick(Window: HWND; Message: UINT; Id, Time: LongWord);
var
  Remaining: Integer;
begin
  if (ProgressTimer = 0) or (Id <> ProgressTimer) or not ProgressSurface.Visible then Exit;
  Remaining := TargetProgress - DrawnProgress;
  if Remaining > 0 then
    DrawnProgress := DrawnProgress + (Remaining + 4) div 5
  else
    DrawnProgress := TargetProgress;
  HighlightPosition := HighlightPosition + ScaleX(5);
  if HighlightPosition > ProgressBarImage.Width + ScaleX(150) then HighlightPosition := 0;
  DrawInstallProgress;
end;

procedure StopInstallProgressTimer;
var
  Timer: LongWord;
begin
  Timer := ProgressTimer;
  ProgressTimer := 0;
  if Timer <> 0 then KillProgressTimer(0, Timer);
end;

function NewProgressLabel(Parent: TWinControl; const Caption: String; Top, Height, FontSize: Integer;
  Background, Foreground: TColor): TNewStaticText;
begin
  Result := TNewStaticText.Create(WizardForm);
  Result.Parent := Parent;
  Result.StyleElements := [];
  Result.Color := Background;
  CenterSetupLabel(Result, Parent.ClientWidth, Top, Height, FontSize, Foreground);
  Result.Caption := Caption;
end;

procedure InitializeInstallProgress;
var
  CardWidth, CardTop, CardHeight, ContentTop: Integer;
  Logo: TBitmapImage;
  Heading: TNewStaticText;
begin
  ProgressSurface := TPanel.Create(WizardForm);
  ProgressSurface.Parent := WizardForm;
  ProgressSurface.SetBounds(0, 0, WizardForm.ClientWidth, WizardForm.ClientHeight);
  ProgressSurface.Anchors := [akLeft, akTop, akRight, akBottom];
  ProgressSurface.StyleElements := [];
  ProgressSurface.Color := ProgressBackground;
  ProgressSurface.BevelOuter := bvNone;
  ProgressSurface.Visible := False;

  { Center the 286-unit layout, with a smaller inset on short displays. }
  ContentTop := (ProgressSurface.ClientHeight - ScaleY(286)) div 2;
  if ContentTop < ScaleY(4) then ContentTop := ScaleY(4);
  Logo := SetupArt(ProgressSurface, 'workflow-welcome.png', 0, 96);
  Logo.Top := ContentTop;
  Logo.BackColor := ProgressBackground;
  Heading := NewProgressLabel(ProgressSurface, 'Installing Uninstaller 77', 0, 36, 20,
    ProgressBackground, {#ThemeText});
  Heading.Top := ContentTop + ScaleY(107);

  CardWidth := ProgressSurface.ClientWidth - ScaleX(64);
  if CardWidth > ScaleX(490) then CardWidth := ScaleX(490);
  CardTop := ContentTop + ScaleY(156);
  CardHeight := ScaleY(130);
  ProgressCard := TPanel.Create(WizardForm);
  ProgressCard.Parent := ProgressSurface;
  ProgressCard.SetBounds((ProgressSurface.ClientWidth - CardWidth) div 2, CardTop, CardWidth, CardHeight);
  ProgressCard.StyleElements := [];
  ProgressCard.BevelOuter := bvNone;
  ProgressCard.Color := ProgressCardColor;

  ProgressPercent := NewProgressLabel(ProgressCard, '0%', 12, 42, 24, ProgressCardColor, ProgressGreen);
  ProgressBarImage := TBitmapImage.Create(WizardForm);
  ProgressBarImage.Parent := ProgressCard;
  ProgressBarImage.SetBounds(ScaleX(22), ScaleY(62), ProgressCard.ClientWidth - ScaleX(44), ScaleY(10));
  ProgressBarImage.Bitmap.Width := ProgressBarImage.Width;
  ProgressBarImage.Bitmap.Height := ProgressBarImage.Height;
  ProgressStage := NewProgressLabel(ProgressCard, 'Copying application files...', 88, 24, 10,
    ProgressCardColor, {#ThemeMuted});
end;

procedure ShowInstallProgress(Visible: Boolean);
var
  AnimationsEnabled: LongWord;
begin
  StopInstallProgressTimer;
  ProgressSurface.Visible := Visible;
  if not Visible then Exit;

  TargetProgress := 0;
  DrawnProgress := 0;
  HighlightPosition := 0;
  ProgressPercent.Caption := '0%';
  ProgressStage.Caption := 'Copying application files...';
  ProgressSurface.BringToFront;
  DrawInstallProgress;
  if ReadClientAnimations(SPI_GETCLIENTAREAANIMATION, 0, AnimationsEnabled, 0) and (AnimationsEnabled <> 0) then
    ProgressTimer := SetProgressTimer(0, 0, 33, CreateCallback(@InstallProgressTick));
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if (MaxProgress <= 0) or not ProgressSurface.Visible then Exit;
  if CurProgress >= MaxProgress then TargetProgress := ProgressScale
  else if CurProgress <= 0 then TargetProgress := 0
  else TargetProgress := Trunc(1.0 * ProgressScale * CurProgress / MaxProgress);
  ProgressPercent.Caption := IntToStr(TargetProgress div 10) + '%';
  if (ProgressTimer = 0) or (TargetProgress = ProgressScale) then begin
    DrawnProgress := TargetProgress;
    DrawInstallProgress;
  end;
end;

procedure ShowInstallVerification;
begin
  if not ProgressSurface.Visible then Exit;
  ProgressStage.Caption := 'Verifying the installation...';
  ProgressStage.Update;
end;

procedure DeinitializeSetup;
begin
  StopInstallProgressTimer;
end;
