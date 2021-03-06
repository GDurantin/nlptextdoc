﻿using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.Storage.Streams;

namespace nlptextdoc.image
{
    class FilesManager
    {
        static string OUTPUT_DIR = "nlptextdoc.image";

        static StreamReader OpenReaderForEmbeddedFile(string relativeFilePath)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "nlptextdoc.image." + relativeFilePath.Replace('/', '.');
            var stream = assembly.GetManifestResourceStream(resourceName);
            return new StreamReader(stream);
        }

        internal static string ReadStringFromEmbeddedFile(string relativeFilePath)
        {
            using (var reader = OpenReaderForEmbeddedFile(relativeFilePath))
            {
                return reader.ReadToEnd();
            }
        }

        internal static IEnumerable<string> ReadLinesFromEmbeddedFile(string relativeFilePath)
        {
            using (var reader = OpenReaderForEmbeddedFile(relativeFilePath))
            {
                string line = null;
                while ((line = reader.ReadLine()) != null)
                {
                    yield return line;
                }
            }
        }

        internal async static Task<StorageFolder> GetOutputFolderAsync()
        {
            var picturesFolder = KnownFolders.SavedPictures;
            var outputFolder = await picturesFolder.CreateFolderAsync(OUTPUT_DIR, CreationCollisionOption.OpenIfExists);
            return outputFolder;
        }

        internal static string GetOutputFolderPath()
        {
            return Task.Run(GetOutputFolderAsync).Result.Path;
        }

        internal static async Task<string> GetLastFileName() 
        {
            var fileTypeFilter = new List<string>();
            fileTypeFilter.Add(".json");
            QueryOptions queryOptions = new QueryOptions(CommonFileQuery.OrderByName, fileTypeFilter);            

            var folder = await GetOutputFolderAsync();
            StorageFileQueryResult results = folder.CreateFileQueryWithOptions(queryOptions);
            IReadOnlyList<StorageFile> sortedFiles = await results.GetFilesAsync();
            var lastFile = sortedFiles.LastOrDefault();

            if(lastFile == null) { return null;  } else { return lastFile.Name; }
        }

        internal static int GetLastSavedCounter()
        {
            var lastFileName = Task.Run(GetLastFileName).Result;
            if(lastFileName == null)
            {
                return 0;
            }
            else
            {
                return Int32.Parse(lastFileName.Substring(0, 5));
            }
        }

        internal static async Task WriteTextToFileAsync(string fileName, string text)
        {
            var outputFolder = await GetOutputFolderAsync();
            var textFile = await outputFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            using (var stream = await textFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                using (IOutputStream outputStream = stream.GetOutputStreamAt(0))
                {
                    using (DataWriter dataWriter = new DataWriter(outputStream))
                    {
                        dataWriter.WriteString(text);
                        await dataWriter.StoreAsync();
                        dataWriter.DetachStream();
                    }
                    await outputStream.FlushAsync();
                }
            }
        }

        internal static async Task WriteImageToFileAsync(string fileName, int width, int height, byte[] pixels)
        {
            var outputFolder = await GetOutputFolderAsync();
            var imageFile = await outputFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId, 
                await imageFile.OpenAsync(FileAccessMode.ReadWrite));
            
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                (uint)width,
                (uint)height,
                DisplayInformation.GetForCurrentView().LogicalDpi,
                DisplayInformation.GetForCurrentView().LogicalDpi,
                pixels);
            await encoder.FlushAsync();
        }

        internal static async Task WriteImageToFileAsync(string fileName, Image<Gray8> image, PngBitDepth bitDepth = PngBitDepth.Bit1)
        {
            var outputFolder = await GetOutputFolderAsync();
            var imageFile = await outputFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            using (var stream = await imageFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                using (IOutputStream outputStream = stream.GetOutputStreamAt(0))
                {
                    var encoder = new PngEncoder();
                    encoder.ColorType = PngColorType.Grayscale;
                    encoder.BitDepth = bitDepth;
                    image.Save(outputStream.AsStreamForWrite(), encoder);
                    await outputStream.FlushAsync();
                }
            }
        }
    }
}
