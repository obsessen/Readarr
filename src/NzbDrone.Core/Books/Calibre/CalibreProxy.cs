using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Rest;

namespace NzbDrone.Core.Books.Calibre
{
    public interface ICalibreProxy
    {
        void GetLibraryInfo(CalibreSettings settings);
        CalibreImportJob AddBook(BookFile book, CalibreSettings settings);
        void AddFormat(BookFile file, CalibreSettings settings);
        void RemoveFormats(int calibreId, IEnumerable<string> formats, CalibreSettings settings);
        void SetFields(BookFile file, CalibreSettings settings);
        CalibreBookData GetBookData(int calibreId, CalibreSettings settings);
        long ConvertBook(int calibreId, CalibreConversionOptions options, CalibreSettings settings);
        List<string> GetAllBookFilePaths(CalibreSettings settings);
        CalibreBook GetBook(int calibreId, CalibreSettings settings);
    }

    public class CalibreProxy : ICalibreProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly Logger _logger;

        public CalibreProxy(IHttpClient httpClient,
                            IMapCoversToLocal mediaCoverService,
                            Logger logger)
        {
            _httpClient = httpClient;
            _mediaCoverService = mediaCoverService;
            _logger = logger;
        }

        public CalibreImportJob AddBook(BookFile book, CalibreSettings settings)
        {
            var jobid = (int)(DateTime.UtcNow.Ticks % 1000000000);
            var addDuplicates = false;
            var path = book.Path;
            var filename = $"$dummy{Path.GetExtension(path)}";
            var body = File.ReadAllBytes(path);

            _logger.Trace($"Read {body.Length} bytes from {path}");

            try
            {
                var builder = GetBuilder($"cdb/add-book/{jobid}/{addDuplicates}/{filename}", settings);

                var request = builder.Build();
                request.SetContent(body);

                return _httpClient.Post<CalibreImportJob>(request).Resource;
            }
            catch (RestException ex)
            {
                throw new CalibreException("Unable to add file to calibre library: {0}", ex, ex.Message);
            }
        }

        public void AddFormat(BookFile file, CalibreSettings settings)
        {
            var format = Path.GetExtension(file.Path);
            var bookData = Convert.ToBase64String(File.ReadAllBytes(file.Path));

            var payload = new
            {
                changes = new
                {
                    added_formats = new[]
                    {
                        new
                        {
                            ext = format,
                            data_url = bookData
                        }
                    }
                },
                loaded_book_ids = new[] { file.CalibreId }
            };

            ExecuteSetFields(file.CalibreId, payload, settings);
        }

        public void RemoveFormats(int calibreId, IEnumerable<string> formats, CalibreSettings settings)
        {
            var payload = new
            {
                changes = new
                {
                    removed_formats = formats
                },

                loaded_book_ids = new[] { calibreId }
            };

            ExecuteSetFields(calibreId, payload, settings);
        }

        public void SetFields(BookFile file, CalibreSettings settings)
        {
            var book = file.Album.Value;

            var cover = book.Images.FirstOrDefault(x => x.CoverType == MediaCoverTypes.Cover);
            string image = null;
            if (cover != null)
            {
                var imageFile = _mediaCoverService.GetCoverPath(book.Id, MediaCoverEntity.Album, cover.CoverType, cover.Extension, null);

                if (File.Exists(imageFile))
                {
                    var imageData = File.ReadAllBytes(imageFile);
                    image = Convert.ToBase64String(imageData);
                }
            }

            var payload = new
            {
                changes = new
                {
                    title = book.Title,
                    authors = new[] { file.Artist.Value.Name },
                    cover = image,
                    pubdate = book.ReleaseDate,
                    comments = book.Overview,
                    rating = book.Ratings.Value * 2,
                    identifiers = new Dictionary<string, string>
                    {
                        { "goodreads", book.GoodreadsId.ToString() },
                        { "isbn", book.Isbn13 },
                        { "asin", book.Asin }
                    }
                },
                loaded_book_ids = new[] { file.CalibreId }
            };

            ExecuteSetFields(file.CalibreId, payload, settings);
        }

        private void ExecuteSetFields(int id, object payload, CalibreSettings settings)
        {
            _logger.Trace($"Setting fields:\n{payload.ToJson()}");

            var builder = GetBuilder($"cdb/set-fields/{id}", settings)
                .Post()
                .SetHeader("Content-Type", "application/json");

            var request = builder.Build();
            request.SetContent(payload.ToJson());

            _httpClient.Execute(request);
        }

        public CalibreBookData GetBookData(int calibreId, CalibreSettings settings)
        {
            try
            {
                var builder = GetBuilder($"conversion/book-data/{calibreId}", settings);

                var request = builder.Build();

                return _httpClient.Get<CalibreBookData>(request).Resource;
            }
            catch (RestException ex)
            {
                throw new CalibreException("Unable to add file to calibre library: {0}", ex, ex.Message);
            }
        }

        public long ConvertBook(int calibreId, CalibreConversionOptions options, CalibreSettings settings)
        {
            try
            {
                var builder = GetBuilder($"conversion/start/{calibreId}", settings);

                var request = builder.Build();
                request.SetContent(options.ToJson());

                var jobId = _httpClient.Post<long>(request).Resource;

                // Run async task to check if conversion complete
                _ = PollConvertStatus(jobId, settings);

                return jobId;
            }
            catch (RestException ex)
            {
                throw new CalibreException("Unable to start calibre conversion: {0}", ex, ex.Message);
            }
        }

        public CalibreBook GetBook(int calibreId, CalibreSettings settings)
        {
            try
            {
                var builder = GetBuilder($"ajax/book/{calibreId}", settings);

                var request = builder.Build();
                var response = _httpClient.Get<CalibreBook>(request);

                return response.Resource;
            }
            catch (RestException ex)
            {
                throw new CalibreException("Unable to connect to calibre library: {0}", ex, ex.Message);
            }
        }

        public List<string> GetAllBookFilePaths(CalibreSettings settings)
        {
            try
            {
                var builder = GetBuilder($"ajax/books", settings);

                var request = builder.Build();
                var response = _httpClient.Get<Dictionary<int, CalibreBook>>(request);

                var paths = response.Resource.Values
                    .Select(b => b?.Formats.Values.OrderBy(f => f.LastModified).FirstOrDefault()?.Path)
                    .Where(x => x.IsNotNullOrWhiteSpace())
                    .ToList();

                return paths;
            }
            catch (RestException ex)
            {
                throw new CalibreException("Unable to connect to calibre library: {0}", ex, ex.Message);
            }
        }

        public void GetLibraryInfo(CalibreSettings settings)
        {
            try
            {
                var builder = GetBuilder($"ajax/library-info", settings);
                var request = builder.Build();
                var response = _httpClient.Execute(request);
            }
            catch (RestException ex)
            {
                throw new CalibreException("Unable to connect to calibre library: {0}", ex, ex.Message);
            }
        }

        private HttpRequestBuilder GetBuilder(string relativePath, CalibreSettings settings)
        {
            var baseUrl = HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase);
            baseUrl = HttpUri.CombinePath(baseUrl, relativePath);

            var builder = new HttpRequestBuilder(baseUrl)
                .Accept(HttpAccept.Json);

            builder.LogResponseContent = true;

            if (settings.Username.IsNotNullOrWhiteSpace())
            {
                builder.NetworkCredential = new NetworkCredential(settings.Username, settings.Password);
            }

            return builder;
        }

        private async Task PollConvertStatus(long jobId, CalibreSettings settings)
        {
            var builder = GetBuilder($"/conversion/status/{jobId}", settings);
            var request = builder.Build();

            while (true)
            {
                var status = _httpClient.Get<CalibreConversionStatus>(request).Resource;

                if (!status.Running)
                {
                    if (!status.Ok)
                    {
                        _logger.Warn("Calibre conversion failed.\n{0}\n{1}", status.Traceback, status.Log);
                    }

                    return;
                }

                await Task.Delay(2000);
            }
        }
    }
}