using System.Diagnostics;
using CCPad.CodexQuota;
var root=Path.Combine(Path.GetTempPath(),"ccpad-login-flow-"+Guid.NewGuid());Directory.CreateDirectory(root);
int checks=0, starts=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
Process Fake(int code) { starts++; return Process.Start(new ProcessStartInfo {FileName=Path.Combine(Environment.SystemDirectory,"cmd.exe"),Arguments="/d /c exit /b "+code,UseShellExecute=false,CreateNoWindow=true})!; }
async Task<CodexLoginOutcome> Run(int code,bool login)=>await CodexLoginService.RunAsync(Path.Combine(root,Guid.NewGuid().ToString()),_=>Fake(code),_=>login,CancellationToken.None);
Check(await Run(0,true)==CodexLoginOutcome.Completed,"zero exit and validated fixture completes");
Check(await Run(0,false)==CodexLoginOutcome.Cancelled,"zero exit no fixture login cancels");
Check(await Run(7,false)==CodexLoginOutcome.Failed,"nonzero CLI failure is not cancellation");
Check(await Run(7,true)==CodexLoginOutcome.Failed,"stale valid login does not hide CLI failure");
Check(await Run(9009,false)==CodexLoginOutcome.CliMissing,"cmd missing CLI code");
var before=starts;using var cancelled=new CancellationTokenSource();cancelled.Cancel();
Check(await CodexLoginService.RunAsync(Path.Combine(root,"pre-cancel"),_=>Fake(0),_=>false,cancelled.Token)==CodexLoginOutcome.Cancelled && starts==before,"pre-cancel starts no process");
var blocked=Path.Combine(root,"file");File.WriteAllText(blocked,"fixture");
Check(await CodexLoginService.RunAsync(blocked,_=>Fake(0),_=>false,CancellationToken.None)==CodexLoginOutcome.Failed && starts==before,"directory IO failure is caught before start");
Check(await CodexLoginService.RunAsync(root,_=>null,_=>false,CancellationToken.None)==CodexLoginOutcome.Failed,"null process fails");
Check(await CodexLoginService.RunAsync(root,_=>throw new InvalidOperationException(),_=>false,CancellationToken.None)==CodexLoginOutcome.Failed,"start exception fails");
Process Sleep()=>Process.Start(new ProcessStartInfo {FileName=Path.Combine(Environment.SystemDirectory,"cmd.exe"),Arguments="/d /c powershell -NoProfile -Command Start-Sleep -Seconds 30",UseShellExecute=false,CreateNoWindow=true})!;
using var unrelated=Sleep();using var runningCancel=new CancellationTokenSource();int owned=0;
try {
 var task=CodexLoginService.RunAsync(Path.Combine(root,"owned"),_=>{var p=Sleep();owned=p.Id;return p;},_=>false,runningCancel.Token);
 runningCancel.Cancel();
 Check(await task.WaitAsync(TimeSpan.FromSeconds(10))==CodexLoginOutcome.Cancelled,"active wait cancellation completes");
 bool ownedAlive;try{using var p=Process.GetProcessById(owned);ownedAlive=!p.HasExited;}catch(ArgumentException){ownedAlive=false;}
 Check(!ownedAlive && !unrelated.HasExited,"only owned process stopped; unrelated session survives");
} finally {if(!unrelated.HasExited)unrelated.Kill(entireProcessTree:true);await unrelated.WaitForExitAsync();}
Console.WriteLine($"{checks}/{checks} login flow checks; realCodexStarts=0; realCredentialsRead=0");
