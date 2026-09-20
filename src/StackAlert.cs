using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Windows.Forms;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;

public static class Program {
 [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
 [STAThread] public static void Main(string[] args) {
  SetProcessDPIAware();
  Application.EnableVisualStyles();
  Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
  Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message,"运行出错"); };
  try { if(args.Length>0 && args[0]=="--test") { Tests.Run(args); return; } Application.Run(new MainForm()); }
  catch(Exception e) { MessageBox.Show(e.Message,"100层提醒器"); }
 }
}
public class Detector {
 public static double Score(Bitmap a, Bitmap b) {
  if(a.Size!=b.Size) return 0;
  double sumA=0,sumB=0,sumAA=0,sumBB=0,sumAB=0; int n=a.Width*a.Height;
  for(int y=0;y<a.Height;y++) for(int x=0;x<a.Width;x++) {
   Color ca=a.GetPixel(x,y), cb=b.GetPixel(x,y);
   double va=(ca.R+ca.G)*0.5-ca.B*0.5, vb=(cb.R+cb.G)*0.5-cb.B*0.5;
   sumA+=va; sumB+=vb; sumAA+=va*va; sumBB+=vb*vb; sumAB+=va*vb;
  }
  double den=Math.Sqrt(Math.Max(0,(sumAA-sumA*sumA/n)*(sumBB-sumB*sumB/n)));
  return den<1?0:(sumAB-sumA*sumB/n)/den;
 }
}
public class Gate {
 int hits; bool fired; long belowSince=-1, lastHit=-1, lastObservation=-1;
 public void Suspend() {hits=0;belowSince=-1;lastHit=-1;lastObservation=-1;}
 public string State {get {return fired?"本轮已提醒，等待层数下降":(hits>0?"正在确认满层":"已就绪，等待满层");}}
 public bool Step(double score, double threshold, long now) {
  if(lastObservation>=0&&now-lastObservation>250)Suspend();
  lastObservation=now;
  if(score>=threshold) {
   belowSince=-1;
   hits=(lastHit>=0&&now-lastHit<=200)?hits+1:1;lastHit=now;
   if(!fired&&(hits>=2||score>=Math.Max(0.96,threshold))) {fired=true;return true;}
  } else {
   hits=0;lastHit=-1;
   // Only a clear mismatch rearms; values near the boundary are inconclusive.
   if(score<threshold-0.10) {
    if(belowSince<0)belowSince=now;
    if(now-belowSince>=150)fired=false;
   } else belowSince=-1;
  }
  return false;
 }
}
public class MainForm:Form {
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,System.Text.StringBuilder text,int max);
 readonly string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"D4StackAlert");
 Bitmap template; Rectangle region; Gate gate=new Gate(); bool running;
 SpeechSynthesizer speech; Timer timer=new Timer();
 Label status=new Label(); PictureBox preview=new PictureBox(); Button start=new Button();
 NumericUpDown threshold=new NumericUpDown(); CheckBox gameOnly=new CheckBox();
 Stopwatch watch=Stopwatch.StartNew(); long previousTick=-1; Bitmap latest;
 Queue<string> records=new Queue<string>(); string lastAlert="尚未触发";
 void Record(string value) { records.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff")+","+value); while(records.Count>2400)records.Dequeue(); }
 public MainForm() {
  Text="暗黑4 · 100层语音提醒 v2"; ClientSize=new Size(520,425); FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
  Font=new Font("Microsoft YaHei UI",10); StartPosition=FormStartPosition.CenterScreen;
  Label help=new Label {Text="首次使用：游戏满100层时，框选完整的“100”数字。\n之后点击开始，再切回游戏。满层持续时只播报一次。",Location=new Point(18,15),Size=new Size(485,55)}; Controls.Add(help);
  Button select=new Button {Text="① 框选100数字",Location=new Point(18,80),Size=new Size(160,38)};
  select.Click+=delegate { Calibrate(); }; Controls.Add(select);
  start.Text="② 开始监测"; start.SetBounds(190,80,145,38); start.Click+=delegate { if(running) Stop(); else {if(template==null) {MessageBox.Show("请先在满100层时框选数字。"); return;} running=true; gate=new Gate(); start.Text="暂停监测"; status.Text="监测中，请切回游戏。";} }; Controls.Add(start);
  Button test=new Button {Text="试听语音",Location=new Point(347,80),Size=new Size(150,38)}; test.Click+=delegate {Speak();}; Controls.Add(test);
  preview.SetBounds(18,133,150,65); preview.SizeMode=PictureBoxSizeMode.Zoom; preview.BackColor=Color.FromArgb(35,35,35); Controls.Add(preview);
  Label label=new Label {Text="匹配阈值（默认85%）",Location=new Point(190,140),AutoSize=true}; Controls.Add(label);
  threshold.SetBounds(385,136,95,30); threshold.Minimum=65; threshold.Maximum=99; threshold.Value=85; Controls.Add(threshold);
  gameOnly.Text="仅当前台窗口标题包含 Diablo / 暗黑时监测"; gameOnly.SetBounds(18,209,490,30); gameOnly.Checked=true; Controls.Add(gameOnly);
  status.SetBounds(18,248,480,68); status.Text="等待框选。框选时只包含数字，尽量不带周围图标。"; Controls.Add(status);
  Button diagnostic=new Button {Text="漏提醒？保存检测记录",Location=new Point(18,320),Size=new Size(250,35)}; diagnostic.Click+=delegate {Export();};Controls.Add(diagnostic);
  Controls.Add(new Label {Text="约每50毫秒检测；高度匹配可立即提醒。\n明确不匹配持续150毫秒后，允许再次提醒。\n漏报后尽快保存记录；分辨率或技能位置变化后重新框选。",Location=new Point(18,365),Size=new Size(490,58),ForeColor=Color.DimGray});
  try {speech=new SpeechSynthesizer(); foreach(var v in speech.GetInstalledVoices()) if(v.Enabled&&v.VoiceInfo.Culture.TwoLetterISOLanguageName=="zh") {speech.SelectVoice(v.VoiceInfo.Name);break;} } catch {speech=null;}
  try {string[] p=File.ReadAllText(Path.Combine(folder,"region.txt")).Split(','); region=new Rectangle(int.Parse(p[0]),int.Parse(p[1]),int.Parse(p[2]),int.Parse(p[3])); using(var b=new Bitmap(Path.Combine(folder,"100.png"))) template=new Bitmap(b); preview.Image=template; status.Text="已加载上次框选。点击开始即可。";} catch { }
  timer.Interval=50; timer.Tick+=delegate {Tick();}; timer.Start();
  FormClosed+=delegate {timer.Dispose(); if(speech!=null) speech.Dispose(); if(template!=null) template.Dispose();if(latest!=null)latest.Dispose();};
 }
 void Stop() {running=false; start.Text="② 开始监测";status.Text="已暂停。"; if(speech!=null) speech.SpeakAsyncCancelAll();}
 void Speak() {try {if(speech==null) {System.Media.SystemSounds.Exclamation.Play(); Record("sound_fallback"); status.Text="未找到系统语音，已使用提示音。";return;} speech.SpeakAsyncCancelAll(); speech.SpeakAsync("层数已满");Record("speech_requested");} catch(Exception e) {Record("speech_error,"+e.Message.Replace(',',' '));status.Text="语音失败："+e.Message;System.Media.SystemSounds.Exclamation.Play();}}
 void Export() {
  using(var dialog=new SaveFileDialog {Title="保存最近约2分钟的检测记录",Filter="检测记录 (*.csv)|*.csv",FileName="D4-detection-"+DateTime.Now.ToString("HHmmss")+".csv"}) if(dialog.ShowDialog()==DialogResult.OK) {
   File.WriteAllText(dialog.FileName,"time,event_or_score,threshold,interval_ms,state,trigger\r\n"+string.Join("\r\n",records.ToArray()),System.Text.Encoding.UTF8);
   if(latest!=null)latest.Save(dialog.FileName+".latest.png",System.Drawing.Imaging.ImageFormat.Png);
   if(template!=null)template.Save(dialog.FileName+".template.png",System.Drawing.Imaging.ImageFormat.Png);
   MessageBox.Show("已保存检测记录、最近检测画面和100样本。\n可把这三个文件发来分析。只包含框选区域。","记录已保存");
  }
 }
 void Calibrate() {
  Stop(); Hide();
  try {System.Threading.Thread.Sleep(300); using(var picker=new Picker()) if(picker.ShowDialog()==DialogResult.OK) {
   if(template!=null) template.Dispose(); template=new Bitmap(picker.Result); region=picker.RegionOnScreen; preview.Image=template;
   Directory.CreateDirectory(folder); template.Save(Path.Combine(folder,"100.png"),System.Drawing.Imaging.ImageFormat.Png);
   File.WriteAllText(Path.Combine(folder,"region.txt"),string.Join(",",new object[]{region.X,region.Y,region.Width,region.Height})); status.Text="已记住100层样本。点击开始，然后切回游戏。";
  }} finally {Show();}
 }
 void Tick() {
  if(!running||template==null) return;
  if(gameOnly.Checked) {var title=new System.Text.StringBuilder(512); GetWindowText(GetForegroundWindow(),title,512); string t=title.ToString(); if(GetForegroundWindow()==Handle||(t.IndexOf("Diablo",StringComparison.OrdinalIgnoreCase)<0&&!t.Contains("暗黑"))) {status.Text="等待切回暗黑4游戏窗口…\n上次触发："+lastAlert; Record("foreground_paused");previousTick=-1;gate.Suspend(); return;}}
  try {
   using(var current=new Bitmap(region.Width,region.Height)) {using(var g=Graphics.FromImage(current)) g.CopyFromScreen(region.Location,Point.Empty,region.Size);
    long now=watch.ElapsedMilliseconds, interval=previousTick<0?0:now-previousTick; previousTick=now;
    double score=Detector.Score(template,current); double limit=(double)threshold.Value/100;
    bool alert=gate.Step(score,limit,now);
    if(latest!=null)latest.Dispose();latest=new Bitmap(current);
    Record(string.Format(CultureInfo.InvariantCulture,"{0:F3},{1:F2},{2},{3},{4}",score,limit,interval,gate.State,alert?"ALERT":""));
    if(alert) {lastAlert=DateTime.Now.ToString("HH:mm:ss");Speak();}
    status.Text=string.Format("匹配度 {0:0}% · {1}\n上次触发：{2} · 检测间隔 {3}毫秒",Math.Max(0,score)*100,gate.State,lastAlert,interval);
   }
  } catch(Exception e) {Stop(); status.Text="截图失败："+e.Message;}
 }
}
public class Picker:Form {
 Bitmap screenshot; Point anchor; Rectangle selection; bool dragging;
 public Bitmap Result; public Rectangle RegionOnScreen;
 public Picker() {
  Rectangle desktop=SystemInformation.VirtualScreen; FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual; Bounds=desktop; TopMost=true; DoubleBuffered=true; Cursor=Cursors.Cross; KeyPreview=true;
  screenshot=new Bitmap(desktop.Width,desktop.Height); using(var g=Graphics.FromImage(screenshot)) g.CopyFromScreen(desktop.Location,Point.Empty,desktop.Size);
  KeyDown+=delegate(object s,KeyEventArgs e) {if(e.KeyCode==Keys.Escape) {DialogResult=DialogResult.Cancel;Close();}};
  MouseDown+=delegate(object s,MouseEventArgs e) {if(e.Button==MouseButtons.Left) {anchor=e.Location;dragging=true;}};
  MouseMove+=delegate(object s,MouseEventArgs e) {if(dragging) {selection=Rectangle.FromLTRB(Math.Min(anchor.X,e.X),Math.Min(anchor.Y,e.Y),Math.Max(anchor.X,e.X),Math.Max(anchor.Y,e.Y));Invalidate();}};
  MouseUp+=delegate {if(!dragging)return;dragging=false;if(selection.Width<10||selection.Height<10||selection.Width>350||selection.Height>150) {MessageBox.Show("请只框住完整的100数字（10～350像素宽，10～150像素高）。");return;} Result=screenshot.Clone(selection,screenshot.PixelFormat); RegionOnScreen=new Rectangle(selection.X+Left,selection.Y+Top,selection.Width,selection.Height);DialogResult=DialogResult.OK;Close();};
 }
 protected override void OnPaint(PaintEventArgs e) {e.Graphics.DrawImageUnscaled(screenshot,0,0); using(var shade=new SolidBrush(Color.FromArgb(70,0,0,0))) e.Graphics.FillRectangle(shade,ClientRectangle); if(selection.Width>0) {e.Graphics.DrawImage(screenshot,selection,selection,GraphicsUnit.Pixel); e.Graphics.DrawRectangle(Pens.Lime,selection);} e.Graphics.FillRectangle(Brushes.Black,20,20,650,45);e.Graphics.DrawString("拖动框住完整的 100 数字，松开鼠标保存；Esc 取消。",SystemFonts.MessageBoxFont,Brushes.White,30,35);}
 protected override void Dispose(bool disposing) {if(disposing) {if(screenshot!=null)screenshot.Dispose();if(Result!=null)Result.Dispose();}base.Dispose(disposing);}
}
public static class Tests {
 public static void Run(string[] args) {
  using(var a=new Bitmap(args[1])) using(var b=new Bitmap(args[2])) {
   Rectangle roi=new Rectangle(1459,1345,48,29);
   using(var full=a.Clone(roi,a.PixelFormat)) using(var low=b.Clone(roi,b.PixelFormat)) {
    double same=Detector.Score(full,full), different=Detector.Score(full,low);
    if(same<0.99||different>=0.85) throw new Exception("Screenshot test failed");
    var gate=new Gate(); int count=0; long now=0;
    for(int i=0;i<20;i++,now+=50)if(gate.Step(same,.85,now))count++;
    for(int i=0;i<2;i++,now+=50)gate.Step(different,.85,now);
    for(int i=0;i<10;i++,now+=50)if(gate.Step(same,.85,now))count++;
    if(count!=1)throw new Exception("Brief flicker caused repeat");
    for(int i=0;i<5;i++,now+=50)gate.Step(different,.85,now);
    for(int i=0;i<10;i++,now+=50)if(gate.Step(same,.85,now))count++;
    if(count!=2)throw new Exception("Fast rearm failed");
    var quick=new Gate();if(!quick.Step(.98,.85,0))throw new Exception("Short high confidence full missed");
    var confirm=new Gate();if(confirm.Step(.90,.85,0)||!confirm.Step(.90,.85,50))throw new Exception("Two frame confirmation failed");
    var stale=new Gate();if(stale.Step(.90,.85,0)||stale.Step(.90,.85,1000))throw new Exception("Stale frame confirmed");
    for(int i=0;i<10;i++)gate.Step(.82,.85,now+=50);
    if(gate.Step(same,.85,now+50))throw new Exception("Boundary jitter rearmed");
    File.WriteAllText(args[3],string.Format("100 sample score: {0:F4}\r\n19 sample score: {1:F4}\r\nPASS: sustained full, brief flicker, 250ms recharge, short full, two-frame confirmation, stale-frame rejection, boundary jitter\r\n",same,different));
   }
  }
 }
}
