using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.CommandLine; 
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SharpCompress.Archives;
using SharpCompress.Common;

// rozwiazanie konfliktu nazw miedzy system.io a metadataextractor
using Directory = System.IO.Directory; 

namespace PhotoSorter
{
    class Program
    {
        // sciezki ustawiane z parametrow
        private static string SourceFolder = string.Empty;
        private static string OutputFolder = string.Empty;
        
        // obslugiwane rozszerzenia
        private static readonly string[] ImageExtensions = { ".JPG", ".JPEG", ".PNG" };
        private static readonly string[] ArchiveExtensions = { ".ZIP", ".TAR" };

        static async Task<int> Main(string[] args)
        {
            // konfiguracja system.commandline
            
            // opcja input (domyslnie /input)
            var inputOption = new Option<DirectoryInfo>(
                aliases: new[] { "--input", "-i" },
                description: "folder zrodlowy",
                getDefaultValue: () => new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "input"))
            );

            // opcja output (domyslnie /output)
            var outputOption = new Option<DirectoryInfo>(
                aliases: new[] { "--output", "-o" },
                description: "folder docelowy",
                getDefaultValue: () => new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "output"))
            );

            var rootCommand = new RootCommand("PhotoSorter - sortowanie zdjec");
            rootCommand.AddOption(inputOption);
            rootCommand.AddOption(outputOption);

            // ustawienie handlera
            rootCommand.SetHandler(async (inputDir, outputDir) =>
            {
                await RunSorter(inputDir, outputDir);
            }, inputOption, outputOption);

            return await rootCommand.InvokeAsync(args);
        }

        private static async Task RunSorter(DirectoryInfo inputDir, DirectoryInfo outputDir)
        {
            SourceFolder = inputDir.FullName;
            OutputFolder = outputDir.FullName;

            Console.WriteLine("-------- Uruchamianie PhotoSortera --------");
            
            // sprawdzenie folderow
            if (!Directory.Exists(SourceFolder)) Directory.CreateDirectory(SourceFolder);
            if (!Directory.Exists(OutputFolder)) Directory.CreateDirectory(OutputFolder);

            Console.WriteLine($"input: {SourceFolder}");
            Console.WriteLine($"output: {OutputFolder}");

            // teraz czekamy az wszystko co juz jest sie przemieli
            await ProcessExistingFiles(SourceFolder);

            // watcher do monitorowania na zywo
            using FileSystemWatcher watcher = new FileSystemWatcher(SourceFolder);
            watcher.IncludeSubdirectories = true; 
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
            
            watcher.Created += async (sender, e) => await OnFileCreated(e.FullPath);
            watcher.EnableRaisingEvents = true;

            Console.WriteLine("nacisnij 'q' aby wyjsc");
            while (Console.ReadKey().Key != ConsoleKey.Q) { }
        }

        // teraz zwraca Task i czeka na wszystkie pliki w folderze
        private static async Task ProcessExistingFiles(string path)
        {
            var tasks = new List<Task>();
            foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
            {
                tasks.Add(OnFileCreated(file));
            }
            await Task.WhenAll(tasks);
        }

        private static async Task OnFileCreated(string filePath)
        {
            // ignorujemy smieci macowe (pliki zaczynajace sie od kropki)
            if (Path.GetFileName(filePath).StartsWith(".")) return;

            await WaitForFileAccess(filePath);
            string extension = Path.GetExtension(filePath).ToUpper();

            try
            {
                if (ImageExtensions.Contains(extension))
                {
                    Console.WriteLine($"znaleziono zdjecie: {Path.GetFileName(filePath)}");
                    await ProcessPhoto(filePath);
                }
                else if (ArchiveExtensions.Contains(extension))
                {
                    Console.WriteLine($"znaleziono archiwum: {Path.GetFileName(filePath)}");
                    // czekamy na koniec przetwarzania archiwum
                    await ProcessArchive(filePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"nie udalo sie przetworzyc {filePath}: {ex.Message}");
            }
        }

        private static async Task ProcessPhoto(string filePath)
        {
            // data i koordynaty z exif
            DateTime photoDate = GetDateTakenFromExif(filePath);
            var coordinates = GetCoordinatesFromExif(filePath);
            string locationPathPart = "UnknownLocation";

            // lokacja z nominatim
            if (coordinates.HasValue)
            {
                var locData = await GeocodingService.GetLocationName(coordinates.Value.lat, coordinates.Value.lon);
                if (locData != null)
                {
                    string country = SanitizeFileName(locData.Value.country ?? "UnknownCountry");
                    string city = SanitizeFileName(locData.Value.city ?? "UnknownCity");
                    locationPathPart = Path.Combine(country, city);
                }
            }

            // rok/miesiac/dzien
            string dateStructure = Path.Combine(
                photoDate.Year.ToString(), 
                photoDate.Month.ToString("D2"), 
                photoDate.Day.ToString("D2")
            );
            
            string destByDate = Path.Combine(OutputFolder, "ByDate", dateStructure);
            string destByLocation = Path.Combine(OutputFolder, "ByLocation", locationPathPart);

            CopyFileTo(filePath, destByDate);
            CopyFileTo(filePath, destByLocation);

            Console.WriteLine($"przetworzono: {Path.GetFileName(filePath)} -> {dateStructure} | {locationPathPart}");
        }

        private static async Task ProcessArchive(string archivePath)
        {
            // folder tymczasowy
            string tempDir = Path.Combine(Path.GetTempPath(), "PhotoSorter_" + Guid.NewGuid());

            try
            {
                Directory.CreateDirectory(tempDir);

                try
                {
                    // rozpakowanie
                    using (var archive = ArchiveFactory.Open(archivePath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (!entry.IsDirectory)
                            {
                                entry.WriteToDirectory(tempDir, new ExtractionOptions 
                                { 
                                    ExtractFullPath = true, 
                                    Overwrite = true 
                                });
                            }
                        }
                    }
                    // czekamy az zdjecia z tempa sie skopiuja zanim wejdzie finally
                    await ProcessExistingFiles(tempDir);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"blad archiwum {Path.GetFileName(archivePath)}: {ex.Message}");
                }
            }
            finally
            {
                // czyszczenie tempdir - wykona sie dopiero jak wszystko powyzej (w tym await) skonczy
                if (Directory.Exists(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (Exception cleanupEx)
                    {
                        Console.WriteLine($"blad usuwania temp {tempDir}: {cleanupEx.Message}");
                    }
                }
            }
        }

        private static void CopyFileTo(string sourceFile, string destFolder)
        {
            Directory.CreateDirectory(destFolder);
            string destFile = Path.Combine(destFolder, Path.GetFileName(sourceFile));
            
            int counter = 1;
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(destFile);
            string ext = Path.GetExtension(destFile);
            
            while (File.Exists(destFile))
            {
                destFile = Path.Combine(destFolder, $"{fileNameWithoutExt}_{counter++}{ext}");
            }

            File.Copy(sourceFile, destFile);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static async Task WaitForFileAccess(string filePath)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    await Task.Delay(1000);
                }
            }
        }

        private static DateTime GetDateTakenFromExif(string filePath)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(filePath);
                var subIfdDirectory = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfdDirectory != null && subIfdDirectory.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out DateTime date))
                {
                    return date;
                }
            }
            catch { }
            return File.GetCreationTime(filePath);
        }

        private static (double lat, double lon)? GetCoordinatesFromExif(string filePath)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(filePath);
                var gps = directories.OfType<GpsDirectory>().FirstOrDefault();

                if (gps == null) return null;

                var latArr = gps.GetRationalArray(GpsDirectory.TagLatitude);
                var latRef = gps.GetString(GpsDirectory.TagLatitudeRef);
                var lonArr = gps.GetRationalArray(GpsDirectory.TagLongitude);
                var lonRef = gps.GetString(GpsDirectory.TagLongitudeRef);

                if (latArr != null && lonArr != null && latArr.Length == 3 && lonArr.Length == 3)
                {
                    double latitude = latArr[0].ToDouble() + (latArr[1].ToDouble() / 60.0) + (latArr[2].ToDouble() / 3600.0);
                    double longitude = lonArr[0].ToDouble() + (lonArr[1].ToDouble() / 60.0) + (lonArr[2].ToDouble() / 3600.0);

                    if (latRef == "S") latitude = -latitude;
                    if (lonRef == "W") longitude = -longitude;

                    if (latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180)
                    {
                        return (latitude, longitude);
                    }
                }
            }
            catch { }
            return null;
        }
    }
}