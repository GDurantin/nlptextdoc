﻿using Abot.Crawler;
using Abot.Poco;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Dom.Html;
using AngleSharp.Network;
using nlptextdoc.text.document;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace nlptextdoc.extract.html
{
    public class WebsiteTextExtractor : IDisposable
    {        
        public WebsiteTextExtractor(WebsiteExtractorParams extractorParams)
        {
            // Save params
            ExtractorParams = extractorParams;
            
            Init();
        }        

        public WebsiteTextExtractor(string storageDirForWebsite, string[] newParams, bool doContinue = false)
        {
            // Save action requested
            DoContinue = doContinue;

            // Reload params file 
            FileInfo paramFileInfo = new FileInfo(Path.Combine(storageDirForWebsite, LogsDirName, ParamsFileName));
            if(!paramFileInfo.Exists)
            {
                throw new Exception("No parameters file found at : " + paramFileInfo.FullName);
            }
            using (StreamReader sr = new StreamReader(paramFileInfo.FullName))
            {
                ExtractorParams = WebsiteExtractorParams.ReadFromFile(sr);
            }
            // Override with new params
            if(newParams != null)
            {
                foreach(string keyValueParam in newParams)
                {
                    ExtractorParams.ParseParam(keyValueParam);
                }
            }

            Init();
        }

        private void Init()
        {
            // Initialize the extraction task
            ConfigureWebCrawler(ExtractorParams.RootUrl, ExtractorParams.MaxPageCount, ExtractorParams.MinCrawlDelay);
            ConfigureHtmlParser();

            // Initialize the content directory and log files
            ConfigureStorageDirectories(ExtractorParams.StorageDir);
            InitLogFiles();
        }

        // Action requested : start a new extraction or continue a previous extraction
        public bool DoContinue { get; private set; }

        // Store configuration params
        public WebsiteExtractorParams ExtractorParams { get; private set; }

        // Root URI for web crawler
        public Uri RootUri { get; private set; }

        // Web crawler engine
        private PoliteWebCrawler crawler;

        // Directory where the extracted text files will be stored
        public DirectoryInfo ContentDirectory { get; private set; }

        // Measuring perfs while crawling the website
        public PerfMonitor Perfs { get; private set; }

        private void ConfigureWebCrawler(string rootURI, int maxPagesCount, int minCrawlDelay)
        {
            RootUri = new Uri(rootURI);

            CrawlConfiguration config = new CrawlConfiguration();

            config.MaxConcurrentThreads = Environment.ProcessorCount;
            config.MaxPagesToCrawl = maxPagesCount;
            config.MaxPagesToCrawlPerDomain = 0;
            config.MaxPageSizeInBytes = 0;
            config.UserAgentString = "Mozilla/5.0 (Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko";
            config.HttpProtocolVersion = HttpProtocolVersion.NotSpecified;
            config.CrawlTimeoutSeconds = 0;
            config.IsUriRecrawlingEnabled = false;
            config.IsExternalPageCrawlingEnabled = false;
            config.IsExternalPageLinksCrawlingEnabled = false;
            config.IsRespectUrlNamedAnchorOrHashbangEnabled = false;
            config.DownloadableContentTypes = "text/html, text/plain";
            config.HttpServicePointConnectionLimit = 200;
            config.HttpRequestTimeoutInSeconds = 15;
            config.HttpRequestMaxAutoRedirects = 7;
            config.IsHttpRequestAutoRedirectsEnabled = true;
            config.IsHttpRequestAutomaticDecompressionEnabled = true;
            config.IsSendingCookiesEnabled = false;
            config.IsSslCertificateValidationEnabled = false;
            config.MinAvailableMemoryRequiredInMb = 0;
            config.MaxMemoryUsageInMb = 0;
            config.MaxMemoryUsageCacheTimeInSeconds = 0;
            config.MaxCrawlDepth = 1000;
            config.MaxLinksPerPage = 1000;
            config.IsForcedLinkParsingEnabled = false;
            config.MaxRetryCount = 0;
            config.MinRetryDelayInMilliseconds = 0;

            config.IsRespectRobotsDotTextEnabled = true;
            config.IsRespectMetaRobotsNoFollowEnabled = true;
            config.IsRespectHttpXRobotsTagHeaderNoFollowEnabled = true;
            config.IsRespectAnchorRelNoFollowEnabled = true;
            config.IsIgnoreRobotsDotTextIfRootDisallowedEnabled = false;
            config.RobotsDotTextUserAgentString = "bingbot";
            config.MinCrawlDelayPerDomainMilliSeconds = minCrawlDelay;
            config.MaxRobotsDotTextCrawlDelayInSeconds = 5;

            config.IsAlwaysLogin = false;
            config.LoginUser = "";
            config.LoginPassword = "";
            config.UseDefaultCredentials = false;

            crawler = new PoliteWebCrawler(config);
            crawler.IsInternalUri((candidateUri,rootUri) => HtmlFileUtils.ShouldCrawlUri(ExtractorParams.Scope, candidateUri, rootUri));
            crawler.ShouldCrawlPageLinks(WebCrawler_ShouldCrawlPageLinks);
            crawler.PageCrawlCompletedAsync += WebCrawler_PageCrawlCompletedAsync;

            // DEBUG: uncomment to debug Abot crawl progress
            // crawler.PageCrawlStartingAsync += WebCrawler_PageCrawlStartingAsync;

            // DEBUG: uncomment to debug Abot crawling decisions
            // crawler.PageCrawlDisallowedAsync += WebCrawler_PageCrawlDisallowedAsync;
            // crawler.PageLinksCrawlDisallowedAsync += WebCrawler_PageLinksCrawlDisallowedAsync;
        }

        // Html parser browsing context
        IBrowsingContext context;

        private void ConfigureHtmlParser()
        {
            // Html parsing config for AngleSharp : load and interpret Css stylesheets
            var config = Configuration.Default
                .WithDefaultLoader(loaderConfig => { loaderConfig.IsResourceLoadingEnabled = true; loaderConfig.Filter = FilterHtmlAndCssResources; })
                .WithCss(cssConfig => { cssConfig.Options = new AngleSharp.Parser.Css.CssParserOptions() { FilterDisplayAndVisibilityOnly = true }; });

            context = BrowsingContext.New(config);
            context.Parsed += TrackParsedFilesSize; // used to measure perfs

            // DEBUG : uncomment to debug Anglesharp network requests 
            //context.Requested += HtmlParser_Requested;
            //context.Requesting += HtmlParser_Requesting;

            // DEBUG : uncomment to debug Anglesharp parsing process
            //context.Parsing += HtmlParser_Parsing;
            //context.Parsed += HtmlParser_Parsed;
            //context.ParseError += HtmlParser_ParseError;
        }

        private void TrackParsedFilesSize(object sender, AngleSharp.Dom.Events.Event ev)
        {
            if (ev is HtmlParseEvent)
            {
                var textSize = ((HtmlParseEvent)ev).Document.Source.Length;
                Perfs.AddDownloadSize(textSize);
            }
            else if (ev is CssParseEvent)
            {
                var textSize = ((CssParseEvent)ev).StyleSheet.SourceCode.Text.Length;
                Perfs.AddDownloadSize(textSize);
            }
        }        

        // Trick to be able to share the same parsed Html document between Abot and HtmlDocumentConverter
        // We need to activate Css dependencies loading to enable this
        private CrawlDecision WebCrawler_ShouldCrawlPageLinks(CrawledPage crawledPage, CrawlContext crawlContext)
        {
            try
            {
                // Add the page already downloaded by Abot in the document cache
                var htmlDocumentUri = crawledPage.HttpWebResponse.ResponseUri;
                if (!context.ResponseCache.ContainsKey(htmlDocumentUri.AbsoluteUri))
                {
                    var response = VirtualResponse.Create(r =>
                    {
                        r.Address(new Url(htmlDocumentUri.AbsoluteUri))
                            .Status(crawledPage.HttpWebResponse.StatusCode)
                            .Content(crawledPage.Content.Text, crawledPage.Content.Charset);
                        foreach (var header in crawledPage.HttpWebResponse.Headers.AllKeys)
                        {
                            r.Header(header, crawledPage.HttpWebResponse.Headers[header]);
                        }
                    });
                    context.ResponseCache.Add(htmlDocumentUri.AbsoluteUri, response);
                }

                // Parse the page and its Css dependencies whith Anglesharp
                // in the right context, initialized in the constructor
                Stopwatch timer = Stopwatch.StartNew();
                crawledPage.AngleSharpHtmlDocument = context.OpenAsync(htmlDocumentUri.AbsoluteUri).Result as IHtmlDocument;
                timer.Stop();
                Perfs.AddParseTime(timer.ElapsedMilliseconds);

                // Remove page which was just parsed from document cache (not useful anymore)
                context.ResponseCache.Remove(htmlDocumentUri.AbsoluteUri);

                // Don't impact the crawl decision
                return new CrawlDecision() { Allow = true };
            }
            catch(Exception e)
            {
                if (e is ArgumentException)
                {
                    // Do nothing if the key already exists : 
                    // - one exception every 15 minutes is better than a systematic lock on each call
                    // - the crawl decision below will properly avoid analyzing the page twice
                }
                else
                {
                    WriteError("Error while parsing the page " + crawledPage.HttpWebResponse.ResponseUri.AbsoluteUri, e);
                }

                // Don't crawl
                return new CrawlDecision() { Allow = false };
            }
        }

        private void WriteError(string context, Exception e)
        {
            lock (errorWriter)
            {
                errorWriter.WriteLine(DateTime.Now.ToLongTimeString());
                errorWriter.WriteLine(context);
                errorWriter.WriteLine("--------------------");
                errorWriter.WriteLine(e.Message);
                errorWriter.WriteLine(e.StackTrace);
                errorWriter.WriteLine();
                errorWriter.Flush();
            }
        }

        // Utility method to ensure that we load only Css dependencies
        private bool FilterHtmlAndCssResources(IRequest request, INode originator)
        {
            // Load Html documents
            if (originator == null) { return true; }
            // Load Css stylesheets (and their contents) only
            if (originator is IHtmlLinkElement linkElement)
            {
                IElement element = (IElement)originator;
                if (linkElement.Type != null && linkElement.Type.EndsWith("css", StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }
            // Don't load any other type of resource
            return false;
        }

        private void ConfigureStorageDirectories(string storagePath)
        {
            var storageDirectory = new DirectoryInfo(storagePath);
            if (!storageDirectory.Exists)
            {
                storageDirectory.Create();
            }

            string websitePath = HtmlFileUtils.GetWebsitePathFromUri(ExtractorParams.Scope, RootUri);            
            ContentDirectory = new DirectoryInfo(Path.Combine(storageDirectory.FullName, websitePath));
            if (!ContentDirectory.Exists)
            {
                ContentDirectory.Create();
            }
        }

        // Write a log of the main http requests

        private StreamWriter logWriter;
        private StreamWriter errorWriter;

        public static string LogsDirName = "_nlptextdoc";
        public static string ParamsFileName = "params.txt";
        public static string HttpLogFileName = "httprequests.log.csv";
        public static string ExceptionsLogFileName = "exceptions.log.txt";
        public static string CheckpointFileName = "checkpoint.txt";

        private void InitLogFiles()
        {
            var logsDirectory = new DirectoryInfo(Path.Combine(ContentDirectory.FullName, LogsDirName));
            if(!logsDirectory.Exists)
            {
                logsDirectory.Create();
            }

            using(var paramsWriter = new StreamWriter(Path.Combine(logsDirectory.FullName, ParamsFileName), DoContinue))
            {
                if (DoContinue) paramsWriter.WriteLine();
                ExtractorParams.WriteToFile(paramsWriter);
            }

            logWriter = new StreamWriter(Path.Combine(logsDirectory.FullName, HttpLogFileName), DoContinue);
            logWriter.Write("Clock");
            logWriter.Write(";");
            logWriter.Write("Url");
            logWriter.Write(";");
            logWriter.Write("Status code");
            logWriter.Write(";");
            logWriter.Write("Reponse time (ms)");
            logWriter.Write(";");
            logWriter.Write("Download time (ms)");
            logWriter.Write(";");
            logWriter.Write("Content size (bytes)");
            logWriter.Write(";");
            logWriter.Write("Unique text blocks (%)");
            logWriter.Write(";");
            logWriter.Write("Crawl depth");
            logWriter.Write(";");
            logWriter.Write("Parent Url");
            logWriter.Write(";");
            logWriter.Write("Redirected from");
            logWriter.Write(";");
            logWriter.Write("Retry count");
            logWriter.Write(";");
            logWriter.Write("Retry after (s)");
            logWriter.Write(";");
            logWriter.Write("Error message");
            logWriter.WriteLine();

            errorWriter = new StreamWriter(Path.Combine(logsDirectory.FullName, ExceptionsLogFileName), DoContinue);
            log4net.LogManager.SetTextWriter(errorWriter);
        }

        private void LogRequest(CrawledPage crawledPage, float percentUnique)
        {
            lock (logWriter)
            {
                logWriter.Write(crawledPage.RequestStarted.ToString("HH:mm:ss.fff"));
                logWriter.Write(";");
                logWriter.Write(crawledPage.Uri.AbsoluteUri);
                logWriter.Write(";");
                if (crawledPage.HttpWebResponse != null)
                {
                    logWriter.Write(crawledPage.HttpWebResponse.StatusCode);
                }
                logWriter.Write(";");
                logWriter.Write((int)crawledPage.Elapsed);
                if (crawledPage.DownloadContentCompleted.HasValue)
                {
                    logWriter.Write(";");
                    logWriter.Write((int)(crawledPage.DownloadContentCompleted.Value - crawledPage.DownloadContentStarted.Value).TotalMilliseconds);
                    logWriter.Write(";");
                    logWriter.Write(crawledPage.Content.Bytes.Length);
                    logWriter.Write(";");
                    logWriter.Write(percentUnique);
                }
                else
                {
                    logWriter.Write(";");
                    logWriter.Write(";");
                    logWriter.Write(";");
                }
                logWriter.Write(";");
                logWriter.Write(crawledPage.CrawlDepth);
                logWriter.Write(";");
                logWriter.Write(crawledPage.ParentUri != null ? crawledPage.ParentUri.AbsoluteUri : "");
                logWriter.Write(";");
                logWriter.Write(crawledPage.RedirectedFrom != null ? crawledPage.RedirectedFrom.Uri.AbsoluteUri : "");
                if (crawledPage.IsRetry)
                {
                    logWriter.Write(";");
                    logWriter.Write(crawledPage.RetryCount);
                    logWriter.Write(";");
                    logWriter.Write(crawledPage.RetryAfter.Value);
                }
                else
                {
                    logWriter.Write(";");
                    logWriter.Write(";");
                }
                if (crawledPage.WebException != null)
                {
                    logWriter.Write(";");
                    logWriter.Write(ToCsvSafeString(crawledPage.WebException.Message));
                }
                else
                {
                    logWriter.Write(";");
                }
                logWriter.WriteLine();
                logWriter.Flush();
            }
        }

        private static string ToCsvSafeString(string message)
        {
            return message.Replace(';', ',').Replace('\n', ' ');
        }

        public void Dispose()
        {
            logWriter.Dispose();
            logWriter = null;

            errorWriter.Dispose();
            errorWriter = null;
        }

        /// <summary>
        /// Crawl all pages of the website and convert them to NLPTextDocuments
        /// </summary>
        public void ExtractNLPTextDocuments()
        {
            //This is synchronous, it will not go to the next line until the crawl has completed
            Console.WriteLine(">>> From : " + RootUri);
            Console.WriteLine(">>> To   : " + ContentDirectory);
            Console.WriteLine();

            Perfs = new PerfMonitor();
            Perfs.WriteStatusHeader();
            CrawlResult result = crawler.Crawl(RootUri);
            Perfs.EndTime = DateTime.Now;
            Console.WriteLine();
            Console.WriteLine();

            if (result.ErrorOccurred)
                Console.WriteLine(">>> Crawl of {0} completed with error: {1}", result.RootUri.AbsoluteUri, result.ErrorException.Message);
            else
                Console.WriteLine("<<< Crawl of {0} completed.", result.RootUri.AbsoluteUri);
            Console.WriteLine();
        }

        // => called each time a page has been crawled by the web crawler
        private void WebCrawler_PageCrawlCompletedAsync(object sender, PageCrawlCompletedArgs e)
        {
            try
            {
                CrawledPage crawledPage = e.CrawledPage;

                // Exit if the page wasn't crawled successfully
                if (crawledPage.WebException != null || crawledPage.HttpWebResponse.StatusCode != HttpStatusCode.OK)
                {
                    LogRequest(crawledPage, 0);
                    Perfs.AddCrawlError();
                    return;
                }

                // Exit if the page had non content
                if (string.IsNullOrEmpty(crawledPage.Content.Text))
                {
                    LogRequest(crawledPage, 0);
                    return;
                }

                // Get the page and its Css dependencies parsed by Abot whith Anglesharp
                var htmlDocumentUri = crawledPage.HttpWebResponse.ResponseUri;
                var htmlDocument = crawledPage.AngleSharpHtmlDocument;

                // Visit the Html page syntax tree and convert it to NLPTextDocument
                Stopwatch timer = Stopwatch.StartNew();
                var htmlConverter = new HtmlDocumentConverter(htmlDocumentUri.AbsoluteUri, htmlDocument);
                var normalizedTextDocument = htmlConverter.ConvertToNLPTextDocument();
                timer.Stop();

                // Check the percentage of text blocks which are new & unique in this page
                var percentUnique = Perfs.SetPercentUniqueForLastDoc(normalizedTextDocument);
                
                // Log the request results
                LogRequest(crawledPage, percentUnique);

                // Write the NLPTextDocument as a text file on disk
                var fileInfo = HtmlFileUtils.GetFilePathFromUri(ContentDirectory, htmlDocumentUri);
                if (!fileInfo.Directory.Exists)
                {
                    fileInfo.Directory.Create();
                }
                NLPTextDocumentWriter.WriteToFile(normalizedTextDocument, fileInfo.FullName);

                Perfs.AddTextConversion(timer.ElapsedMilliseconds, fileInfo.Length);
                Perfs.WriteStatus();
                
                // Exit if the percentage of new text blocks 
                // over the last 1000 pages is below 10%
                if(Perfs.PercentUniqueForLastDocs < 0.1)
                {
                    Console.WriteLine();
                    Console.WriteLine("Extraction stopped because the % of new textblocks fell below 10%");
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                // Safeguard to make sure that an error 
                // during the processing of a single page 
                // can't stop the whole crawl process                
                WriteError("Error while processing the page : " + e.CrawledPage.HttpWebResponse.ResponseUri.AbsoluteUri,  ex);
            }
        }

        // -----------------------
        // DEBUG output statements
        // -----------------------

        private static void WebCrawler_PageCrawlStartingAsync(object sender, PageCrawlStartingArgs e)
        {
            PageToCrawl pageToCrawl = e.PageToCrawl;
            Console.WriteLine("Abot-About to crawl link {0} which was found on page {1}", pageToCrawl.Uri.AbsoluteUri, pageToCrawl.ParentUri.AbsoluteUri);
        }

        private static void WebCrawler_PageCrawlDisallowedAsync(object sender, PageCrawlDisallowedArgs e)
        {
            PageToCrawl pageToCrawl = e.PageToCrawl;
            Console.WriteLine("Abot-Did not crawl page {0} due to {1}", pageToCrawl.Uri.AbsoluteUri, e.DisallowedReason);
        }

        private static void WebCrawler_PageLinksCrawlDisallowedAsync(object sender, PageLinksCrawlDisallowedArgs e)
        {
            CrawledPage crawledPage = e.CrawledPage;
            Console.WriteLine("Abot-Did not crawl the links on page {0} due to {1}", crawledPage.Uri.AbsoluteUri, e.DisallowedReason);
        }

        private void HtmlParser_Requesting(object sender, Event ev)
        {
            Console.WriteLine("AngleSharp-Requesting: " + ((RequestEvent)ev).Request.Address);
        }

        private void HtmlParser_Requested(object sender, Event ev)
        {
            Console.WriteLine("AngleSharp-Requested: " + ((RequestEvent)ev).Response.StatusCode + " (" + ((RequestEvent)ev).Request.Address + ")");
        }

        private void HtmlParser_Parsing(object sender, AngleSharp.Dom.Events.Event ev)
        {
            if (ev is HtmlParseEvent)
            {
                Console.WriteLine("AngleSharp-Parsing: " + ((HtmlParseEvent)ev).Document.Url);
            }
            else if (ev is CssParseEvent)
            {
                var cssSource = ((CssParseEvent)ev).StyleSheet.Href;
                if (String.IsNullOrEmpty(cssSource))
                {
                    cssSource = ((CssParseEvent)ev).StyleSheet.OwnerNode.LocalName;
                }
                Console.WriteLine("AngleSharp-Parsing: " + cssSource);
            }
        }

        private void HtmlParser_Parsed(object sender, AngleSharp.Dom.Events.Event ev)
        {
            if (ev is HtmlParseEvent)
            {
                Console.WriteLine("AngleSharp-Parsed: " + ((HtmlParseEvent)ev).Document.Source.Length + " HTML chars (" + ((HtmlParseEvent)ev).Document.Url + ")");
            }
            else if (ev is CssParseEvent)
            {
                var cssSource = ((CssParseEvent)ev).StyleSheet.Href;
                if (String.IsNullOrEmpty(cssSource))
                {
                    cssSource = ((CssParseEvent)ev).StyleSheet.OwnerNode.LocalName;
                }
                Console.WriteLine("AngleSharp-Parsed: " + ((CssParseEvent)ev).StyleSheet.Children.Count() + " CSS rules (" + cssSource + ")");
            }
        }

        private void HtmlParser_ParseError(object sender, Event ev)
        {
            if (ev is HtmlErrorEvent)
            {
                Console.WriteLine("AngleSharp-ParseERROR: line " + ((HtmlErrorEvent)ev).Position.Line + ": " + ((HtmlErrorEvent)ev).Message);
            }
            else if (ev is CssErrorEvent)
            {
                Console.WriteLine("AngleSharp-ParseERROR: line " + ((CssErrorEvent)ev).Position.Line + ": " + ((CssErrorEvent)ev).Message);
            }
        }

        public class PerfMonitor
        {
            public PerfMonitor()
            {
                StartTime = DateTime.Now;
                for(int i = 0; i < percentUniqueForLastDocs.Length; i++)
                {
                    percentUniqueForLastDocs[i] = -1;
                }
            }

            // Count converted Html pages only
            public int HtmlPagesCount;
            public int CrawlErrorsCount;

            // Bytes
            public long TotalDownloadSize;
            public long TotalSizeOnDisk;

            // Milliseconds

            public long HtmlParseTime;
            public long TextConvertTime;

            // Track unique text blocks
            private HashSet<int> stringHashes = new HashSet<int>();
            float[] percentUniqueForLastDocs = new float[1000];
            int lastDocIndex = -1;

            internal float SetPercentUniqueForLastDoc(NLPTextDocument document)
            {
                lock (stringHashes)
                {
                    int charCount = 0;
                    int uniqueCharCount = 0;
                    foreach (var str in document.TextStrings)
                    {
                        charCount += str.Length;
                        var hashCode = str.GetHashCode();
                        if (!stringHashes.Contains(hashCode))
                        {
                            stringHashes.Add(hashCode);
                            uniqueCharCount += str.Length;
                        }
                    }
                    var percent = (charCount > 0) ? (uniqueCharCount / (float)charCount) : 1;

                    lastDocIndex++;
                    if (lastDocIndex >= percentUniqueForLastDocs.Length)
                    {
                        lastDocIndex = 0;
                    }
                    percentUniqueForLastDocs[lastDocIndex] = percent;

                    return percent;
                }
            }

            public float PercentUniqueForLastDocs
            {
                get
                {
                    float sum = 0;
                    int count = 0;
                    for(int i = 0; i < percentUniqueForLastDocs.Length; i++)
                    {
                        var percent = percentUniqueForLastDocs[i];
                        if (percent < 0) break;
                        sum += percent;
                        count++;
                    }
                    if(count == 0)
                    {
                        return 1;
                    }
                    else
                    {
                        return sum / count;
                    }
                }
            }

            internal void AddCrawlError()
            {
                Interlocked.Increment(ref CrawlErrorsCount);
            }

            public void AddDownloadSize(int downloadSize)
            {
                Interlocked.Add(ref TotalDownloadSize, downloadSize);
            }

            public void AddParseTime(long parseTime)
            {
                Interlocked.Add(ref HtmlParseTime, parseTime);
            }

            public void AddTextConversion(long conversionTime, long sizeOnDisk)
            {
                Interlocked.Increment(ref HtmlPagesCount);
                Interlocked.Add(ref TextConvertTime, conversionTime);
                Interlocked.Add(ref TotalSizeOnDisk, sizeOnDisk);
            }

            public DateTime StartTime;
            public DateTime EndTime;

            public long ElapsedTime
            {
                get
                {
                    return (EndTime != DateTime.MinValue) ?
                        (long)(EndTime - StartTime).TotalMilliseconds :
                        (long)(DateTime.Now - StartTime).TotalMilliseconds;
                }
            }

            public void WriteStatusHeader()
            {
                Console.WriteLine("Time    | Pages | Errors | Unique  | Download   | Disk       | Parsing | Convert |");
            }

            public void WriteStatus()
            {
                Console.Write("\r{0} | {1,5} | {2,5}  |  {3,3} %  | {4,7:0.0} Mb | {5,7:0.0} Mb | {6} | {7} |",
                    TimeSpan.FromMilliseconds(ElapsedTime).ToString(@"h\:mm\:ss"),
                    HtmlPagesCount,
                    CrawlErrorsCount,
                    (int)(PercentUniqueForLastDocs*100),
                    TotalDownloadSize / 1024.0 / 1024.0,
                    TotalSizeOnDisk / 1024.0 / 1024.0,
                    TimeSpan.FromMilliseconds(HtmlParseTime).ToString(@"h\:mm\:ss"),
                    TimeSpan.FromMilliseconds(TextConvertTime).ToString(@"h\:mm\:ss"));
            }
        }
    }
}

