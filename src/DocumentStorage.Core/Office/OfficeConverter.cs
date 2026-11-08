using System.Diagnostics;

namespace DocumentStorage.Core.Office;

public sealed record OfficeRuntimeStatus(bool Available, string Message);
public sealed class OfficeConverter(string? command = null)
{
    private readonly string executable = string.IsNullOrWhiteSpace(command) ? DefaultCommand() : command;
    private static readonly SemaphoreSlim conversion = new(1, 1);
    public static string DefaultCommand()
    {
        var installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"LibreOffice","program","soffice.com");
        return OperatingSystem.IsWindows()&&File.Exists(installed)?installed:"soffice";
    }
    public async Task<OfficeRuntimeStatus> CheckAsync(CancellationToken token=default)
    {
        try { var result=await RunAsync(["--version"],TimeSpan.FromSeconds(8),token);return new(result==0,result==0?"Legacy Office conversion is ready.":"LibreOffice could not start. Check its installation and service permissions."); }
        catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or TimeoutException)
        {return new(false,"LibreOffice is unavailable to the service. Install LibreOffice, then check again.");}
    }
    public async Task<TextResult> ReadLegacyAsync(string path,CancellationToken token=default)
    {
        var word=Path.GetExtension(path).Equals(".doc",StringComparison.OrdinalIgnoreCase);
        await using(var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
        {
            if(source.Length>50L*1024*1024)return new("","Legacy Office input exceeds limits");
            var header=new byte[8];var count=await source.ReadAsync(header,token);
            if(count!=8||!header.AsSpan().SequenceEqual(new byte[]{0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1}))return new("","Office contents do not match extension");
        }
        await conversion.WaitAsync(token);
        var temporary=Path.Combine(Path.GetTempPath(),"stash-office-"+Guid.NewGuid().ToString("N"));
        try
        {
            if(OperatingSystem.IsWindows())Directory.CreateDirectory(temporary);else Directory.CreateDirectory(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var profile=Path.Combine(temporary,"profile");Directory.CreateDirectory(Path.Combine(profile,"user"));
            await File.WriteAllTextAsync(Path.Combine(profile,"user","registrymodifications.xcu"),Profile,token);
            var input=Path.Combine(temporary,word?"source.doc":"source.xls");
            await using(var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
            await using(var output=new FileStream(input,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                var buffer=new byte[81920];long copied=0;int count;
                while((count=await source.ReadAsync(buffer,token))>0)
                {
                    copied+=count;if(copied>50L*1024*1024)return new("","Legacy Office input exceeds limits");
                    await output.WriteAsync(buffer.AsMemory(0,count),token);
                }
            }
            var format=word?"docx:Office Open XML Text":"xlsx:Calc MS Excel 2007 XML";
            var filter=word?"MS Word 97":"MS Excel 97";
            var code=await RunAsync([$"-env:UserInstallation={new Uri(profile+Path.DirectorySeparatorChar).AbsoluteUri}","--headless","--norestore","--nodefault","--nofirststartwizard","--nolockcheck","--infilter="+filter,"--convert-to",format,"--outdir",temporary,input],TimeSpan.FromSeconds(60),token);
            var converted=Path.Combine(temporary,word?"source.docx":"source.xlsx");
            if(code!=0||!File.Exists(converted))return new("","Office conversion failed or password required");
            if(new FileInfo(converted).Length>100L*1024*1024)return new("","Converted Office document exceeds limits");
            return word?await new Formats.WordModule().ReadAsync(converted,token):await new Formats.SpreadsheetModule().ReadAsync(converted,token);
        }
        catch(System.ComponentModel.Win32Exception){return new("","Office converter unavailable");}
        catch(TimeoutException){return new("","Office conversion timed out");}
        finally
        {
            try{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}catch(IOException){}catch(UnauthorizedAccessException){}
            conversion.Release();
        }
    }
    private async Task<int> RunAsync(string[] args,TimeSpan timeout,CancellationToken token)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Office converter could not start.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout);
        var output=DrainAsync(process.StandardOutput,deadline.Token);var error=DrainAsync(process.StandardError,deadline.Token);
        try{await process.WaitForExitAsync(deadline.Token);await Task.WhenAll(output,error);return process.ExitCode;}
        catch(OperationCanceledException)
        {
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
            await ((Task)Task.WhenAll(output,error)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            token.ThrowIfCancellationRequested();throw new TimeoutException("Office conversion timed out.");
        }
    }
    private static async Task DrainAsync(StreamReader reader,CancellationToken token){var buffer=new char[4096];while(await reader.ReadAsync(buffer.AsMemory(),token)>0){} }
    // Empty isolated profile: no trusted locations; no macros or automatic link updates.
    internal const string Profile="""
        <?xml version="1.0" encoding="UTF-8"?>
        <oor:items xmlns:oor="http://openoffice.org/2001/registry">
          <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop><prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop></item>
          <item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>0</value></prop><prop oor:name="Field" oor:op="fuse"><value>false</value></prop><prop oor:name="Chart" oor:op="fuse"><value>false</value></prop></item>
          <item oor:path="/org.openoffice.Office.Calc/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>1</value></prop></item>
          <item oor:path="/org.openoffice.Office.Calc/Formula/Load"><prop oor:name="OOXMLRecalcMode" oor:op="fuse"><value>1</value></prop><prop oor:name="ODFRecalcMode" oor:op="fuse"><value>1</value></prop></item>
        </oor:items>
        """;
}
