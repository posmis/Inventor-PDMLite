using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PdmLite.Installation;

/// <summary>The elevated child receives the secret only after its PID has been authenticated.</summary>
[SupportedOSPlatform("windows")]
internal sealed class PostgresSecretChannel : IDisposable
{
    public string Name { get; } = "pdmlite-setup-" + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;

    public PostgresSecretChannel()
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // UAC may elevate a different Windows account; CurrentUser DPAPI would not work there.
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        pipe = NamedPipeServerStreamAcl.Create(Name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, acl);
    }

    public async Task SendAsync(Process child, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var connect = pipe.WaitForConnectionAsync(timeout.Token);
            var exit = child.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connect, exit) != connect)
                throw new IOException("Повышенный процесс завершился до получения настроек PostgreSQL.");
            await connect;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != child.Id)
                throw new IOException("Не удалось подтвердить процесс настройки PostgreSQL.");
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(secret.AsMemory(), timeout.Token);
        }
        finally { timeout.Cancel(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public void Dispose() => pipe.Dispose();
}
