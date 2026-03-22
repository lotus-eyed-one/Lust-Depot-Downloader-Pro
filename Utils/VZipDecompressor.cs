using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace LustsDepotDownloaderPro.Utils;

public static class VZipDecompressor
{
    public static byte[] Decompress(byte[] compressedData)
    {
        // Steam uses a custom VZip format, which is essentially Deflate
        // First 2 bytes indicate compression type
        
        if (compressedData.Length < 2)
        {
            return compressedData;
        }

        // Check for VZip signature ('VZ')
        if (compressedData[0] == 'V' && compressedData[1] == 'Z')
        {
            // Skip VZip header (first 7 bytes typically)
            int headerSize = 7;
            return DecompressDeflate(compressedData, headerSize);
        }

        // Try standard deflate
        try
        {
            return DecompressDeflate(compressedData, 0);
        }
        catch
        {
            // Try gzip
            try
            {
                return DecompressGzip(compressedData);
            }
            catch
            {
                // Not compressed or unknown format
                Logger.Debug("Data is not compressed or unknown format");
                return compressedData;
            }
        }
    }

    private static byte[] DecompressDeflate(byte[] data, int offset)
    {
        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var inflater = new InflaterInputStream(input);
        using var output = new MemoryStream();
        
        inflater.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public static byte[] DecompressLzma(byte[] data)
    {
        // LZMA decompression (used in some Steam depots)
        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            
            // LZMA SDK would be needed for full support
            // For now, fall back to raw data
            Logger.Warn("LZMA decompression not fully implemented");
            return data;
        }
        catch
        {
            return data;
        }
    }
}
