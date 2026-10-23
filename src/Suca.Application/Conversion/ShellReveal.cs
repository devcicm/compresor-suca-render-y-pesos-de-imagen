using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SUCA_Compressor.Application.Conversion
{
    /// <summary>
    /// Apertura del Explorador con seleccion de archivo usando la API nativa de Windows
    /// (SHOpenFolderAndSelectItems), que es la misma que usa "Mostrar en carpeta".
    /// Es mas fiable que lanzar "explorer.exe /select": no depende de DDE ni de la linea
    /// de comandos, y devuelve un HRESULT real (0 = exito) en vez de un codigo de proceso
    /// que explorer.exe siempre reporta como 1.
    /// </summary>
    public static class ShellReveal
    {
        private const uint SHParseDisplayNameFlag = 0;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHParseDisplayName(
            string pszName,
            IntPtr pbc,
            out IntPtr ppidl,
            uint sfgaoIn,
            out uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder,
            uint cidl,
            [In] IntPtr[]? apidl,
            uint dwFlags);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        /// <summary>
        /// Selecciona el archivo (o abre la carpeta si la ruta es un directorio).
        /// Devuelve false si Windows devolvio un HRESULT de error.
        /// </summary>
        public static bool TryReveal(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            bool isDirectory = Directory.Exists(fullPath);
            if (!isDirectory && !File.Exists(fullPath))
                return false;

            string? folder = isDirectory ? fullPath : Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return false;

            IntPtr folderPidl = IntPtr.Zero;
            IntPtr filePidl = IntPtr.Zero;

            try
            {
                int hrFolder = SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, SHParseDisplayNameFlag, out _);
                if (hrFolder != 0 || folderPidl == IntPtr.Zero)
                    return false;

                if (isDirectory)
                {
                    // Sin archivo que seleccionar: abrir la carpeta.
                    int hrOpen = SHOpenFolderAndSelectItems(folderPidl, 0, null, 0);
                    return hrOpen == 0;
                }

                int hrFile = SHParseDisplayName(fullPath, IntPtr.Zero, out filePidl, SHParseDisplayNameFlag, out _);
                if (hrFile != 0 || filePidl == IntPtr.Zero)
                    return false;

                int hr = SHOpenFolderAndSelectItems(folderPidl, 1, new[] { filePidl }, 0);
                return hr == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShellReveal failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (filePidl != IntPtr.Zero)
                    ILFree(filePidl);
                if (folderPidl != IntPtr.Zero)
                    ILFree(folderPidl);
            }
        }

        /// <summary>Diagnostico para saber por que no se abre una ventana.</summary>
        public static object Diagnose(string? path)
        {
            bool elevated = false;
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                elevated = new WindowsPrincipal(identity)
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                // sin identidad disponible
            }

            string explorerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

            bool? revealed = null;
            if (!string.IsNullOrWhiteSpace(path))
                revealed = TryReveal(path);

            return new
            {
                os = RuntimeInformation.OSDescription,
                sessionId = Process.GetCurrentProcess().SessionId,
                interactive = Environment.UserInteractive,
                elevated,
                explorerExists = File.Exists(explorerPath),
                explorerProcesses = Process.GetProcessesByName("explorer").Length,
                pathExists = !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)),
                revealed,
                note = elevated
                    ? "SUCA corre como administrador: Windows puede descartar la peticion al Explorador por integridad. Ejecuta SUCA sin elevacion."
                    : "Si no aparece la ventana, comprueba que hay sesion de escritorio (no servicio/contenedor) y que Explorer esta en la misma sesion."
            };
        }
    }
}