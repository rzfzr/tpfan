using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;

sealed class FanService : ServiceBase {
    readonly Controller _c;
    Thread _th;
    public FanService(Controller c) { _c = c; ServiceName = "tpfan"; CanShutdown = true; CanPauseAndContinue = false; }
    protected override void OnStart(string[] args) {
        _th = new Thread(() => {
            try { _c.Run(); }
            catch (Exception e) {
                try { File.AppendAllText(Path.Combine(Program.DataDir, "tpfan.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} FATAL {e}\n"); } catch { }
                Environment.Exit(1);   // non-zero exit -> SCM recovery restarts the service
            }
        }) { IsBackground = false, Name = "fan" };
        _th.Start();
    }
    protected override void OnStop() { _c.Stop(); _th?.Join(5000); }
    protected override void OnShutdown() => OnStop();
}
