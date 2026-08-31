using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace TokensLimitsExtension.Settings;

/// <summary>
/// Stores extension secrets encrypted for the current Windows user by DPAPI.
/// The settings UI only needs to keep a non-secret marker in its regular JSON file.
/// </summary>
internal sealed partial class ProtectedSecretStore : IDisposable
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, string> _protectedValues;
    private int _disposed;

    public ProtectedSecretStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _protectedValues = LoadValues(filePath);
    }

    public string? Get(string key)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string? protectedValue;
        lock (_gate)
        {
            _protectedValues.TryGetValue(key, out protectedValue);
        }

        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(protectedValue)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TokensLimits] ERROR: unable to decrypt secret '{key}': {ex.Message}");
            return null;
        }
    }

    public void Set(string key, string value)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var protectedValue = Convert.ToBase64String(Protect(value));
        lock (_gate)
        {
            _protectedValues[key] = protectedValue;
            SaveValuesLocked();
        }
    }

    public void Remove(string key)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            if (_protectedValues.Remove(key))
            {
                SaveValuesLocked();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _protectedValues.Clear();
        }

        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, string> LoadValues(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize(json, ProtectedSecretStoreJsonContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TokensLimits] ERROR: unable to load protected secrets: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveValuesLocked()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(
                _protectedValues,
                ProtectedSecretStoreJsonContext.Default.DictionaryStringString);
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // The primary operation already reported its error, if any.
            }
        }
    }

    private static byte[] Protect(string value)
    {
        var inputBytes = Encoding.UTF8.GetBytes(value);
        var inputPointer = Marshal.AllocHGlobal(inputBytes.Length);
        try
        {
            Marshal.Copy(inputBytes, 0, inputPointer, inputBytes.Length);
            var input = new DataBlob
            {
                Size = inputBytes.Length,
                Data = inputPointer,
            };
            var output = default(DataBlob);

            if (!CryptProtectData(
                    ref input,
                    "TokensLimitsExtension secret",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    ref output))
            {
                throw new CryptographicException($"CryptProtectData failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            try
            {
                return CopyFromBlob(output);
            }
            finally
            {
                FreeLocalMemory(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    private static byte[] Unprotect(byte[] protectedValue)
    {
        var inputPointer = Marshal.AllocHGlobal(protectedValue.Length);
        try
        {
            Marshal.Copy(protectedValue, 0, inputPointer, protectedValue.Length);
            var input = new DataBlob
            {
                Size = protectedValue.Length,
                Data = inputPointer,
            };
            var output = default(DataBlob);

            if (!CryptUnprotectData(
                    ref input,
                    out var description,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    ref output))
            {
                throw new CryptographicException($"CryptUnprotectData failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            try
            {
                return CopyFromBlob(output);
            }
            finally
            {
                FreeLocalMemory(description);
                FreeLocalMemory(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    private static byte[] CopyFromBlob(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
        {
            return [];
        }

        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void FreeLocalMemory(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            _ = LocalFree(pointer);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;

        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ProtectedSecretStoreJsonContext : JsonSerializerContext
{
}
