using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PhotoSorter
{
    // klasa obslugujaca komunikacje z nominatim api
    public static class GeocodingService
    {
        private static readonly HttpClient _client = new HttpClient();

        static GeocodingService()
        {
            _client.DefaultRequestHeaders.Add("User-Agent", "KrzeminskiPhotoSorter/1.0 (krzeminski.test@gmail.com)");
        }

        public static async Task<(string country, string city)?> GetLocationName(double lat, double lon)
        {
            try
            {
                string url = $"https://nominatim.openstreetmap.org/reverse?format=json&lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

                // nie pozwala czesciej niz raz na sekunde (wymog api)
                await Task.Delay(1100); 

                var response = await _client.GetStringAsync(url);
                var json = JObject.Parse(response);
                var address = json["address"];

                if (address != null)
                {
                    string country = address["country"]?.ToString() ?? "Unknown";
                    // rozne klucze dla miast w zaleznosci od wielkosci
                    string city = address["city"]?.ToString() 
                                  ?? address["town"]?.ToString() 
                                  ?? address["village"]?.ToString() 
                                  ?? "Unknown";

                    return (country, city);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"blad nominatim: {ex.Message}");
            }
            return null;
        }
    }
}