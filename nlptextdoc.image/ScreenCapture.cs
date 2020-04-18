﻿using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace nlptextdoc.image
{
    class ScreenCapture
    {
        internal static (int width, int height) GetViewDimensions(WebView webview)
        {
            return ((int)webview.ActualWidth, (int)webview.ActualHeight);
        }


        internal static void SetViewDimensions(WebView webview, (int width, int height) dims)
        {
            webview.Width = dims.width;
            if (dims.height > 0) webview.Height = dims.height;
        }

        internal static async Task<(int width, int height)> GetContentDimensionsAsync(WebView webview)
        {
            int contentWidth, contentHeight;

            var widthString = await JavascriptInterop.ExecuteJavascriptCodeAsync(webview, "document.body.scrollWidth.toString()");
            if (!int.TryParse(widthString, out contentWidth))
                throw new Exception(string.Format("failure/width:{0}", widthString));

            var heightString = await JavascriptInterop.ExecuteJavascriptCodeAsync(webview, "document.body.scrollHeight.toString()");
            if (!int.TryParse(heightString, out contentHeight))
                throw new Exception(string.Format("failure/height:{0}", heightString));

            return (contentWidth, contentHeight);
        }

        internal static async Task CreateAndSaveScreenshotAsync(WebView webview, Rectangle screenshot, string nameSuffix = "screen")
        {
            // Capture picture in memory
            var brush = new WebViewBrush();
            brush.Stretch = Stretch.Uniform;
            brush.AlignmentY = AlignmentY.Top;
            brush.SetSource(webview);
            brush.Redraw();

            // Display in Rectangle
            screenshot.Width = webview.Width;
            screenshot.Height = webview.Height;
            screenshot.Fill = brush;

            // Get Rectangle pixels
            RenderTargetBitmap rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(screenshot);
            var buffer = await rtb.GetPixelsAsync();

            // Get unique file name from current URL
            string fileName = await GetUniqueFileNameFromURLAsync(webview);

            // Write pixels to disk
            await FilesManager.WriteImageToFileAsync(fileName + "_" + nameSuffix + ".png", (uint)screenshot.Width, (uint)screenshot.Height, buffer.ToArray());
        }

        internal static async Task CreateAndSaveTextBoundingBoxes(WebView webview)
        {
            // Extraction json description of all text bounding boxes
            await JavascriptInterop.InjectJavascriptDefinitionsAsync(webview);
            var textBoundingBoxes = await JavascriptInterop.ExtractTextAsJson(webview, true);

            // Write json description to disk
            string fileName = await GetUniqueFileNameFromURLAsync(webview);
            await FilesManager.WriteTextToFileAsync(fileName + ".json", textBoundingBoxes);
        }

        private static async Task<string> GetUniqueFileNameFromURLAsync(WebView webview)
        {
            var url = await JavascriptInterop.ExecuteJavascriptCodeAsync(webview, "document.location.href");
            var fileName = HtmlFileUtils.GetFileNameFromUri(url);
            return fileName;
        }
    }
}