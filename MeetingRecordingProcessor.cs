using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.CognitiveServices.Language.LUIS.Runtime;
using Microsoft.Azure.CognitiveServices.Language.TextAnalytics;
using Microsoft.Azure.CognitiveServices.Language.TextAnalytics.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;

namespace MeetingRecordingProcessor;

public static class MeetingRecordingProcessor
{
    private static string _analyticEndpoint;
    private static string _analyticApiKey;
    private static string _speechApiEndpoint;
    private static string _speechApiKey;
    private static string _speechApiRegion;

    [FunctionName("ProcessMeetingRecording")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest _,
        [Blob("{containerName}/{blobName}", FileAccess.Read)] CloudBlockBlob recordingBlob,
        ILogger log)
    {
        _analyticEndpoint = Environment.GetEnvironmentVariable("AnalyticApiEndpoint");
        _analyticApiKey = Environment.GetEnvironmentVariable("AnalyticApiKey");
        _speechApiEndpoint = Environment.GetEnvironmentVariable("SpeechApiEndpoint");
        _speechApiKey = Environment.GetEnvironmentVariable("SpeechApiKey");
        _speechApiRegion = Environment.GetEnvironmentVariable("SpeechApiRegion");
        log.LogInformation("Processing meeting recording.");

        // Convert audio recording to text using Speech-to-Text API
        var meetingTranscript = await ConvertAudioToTextAsync(recordingBlob.Uri);

        // Extract key phrases and sentiment using Text Analytic API
        var keyPhrases = await ExtractKeyPhrasesAsync(meetingTranscript);
        var sentiment = await ExtractSentimentAsync(meetingTranscript);

        // Generate summary using natural language generation techniques
        var meetingSummary = GenerateSummary(keyPhrases, sentiment);

        // Save summary to blob storage
        var summaryBlobName = Path.GetFileNameWithoutExtension(recordingBlob.Name) + "-summary.txt";
        var summaryBlob = recordingBlob.Container.GetBlockBlobReference(summaryBlobName);
        await summaryBlob.UploadTextAsync(meetingSummary);

        return new OkObjectResult("Meeting recording processed successfully.");
    }

    private static async Task<string> ConvertAudioToTextAsync(Uri recordingUri)
    {
        var speechConfig = SpeechConfig.FromSubscription(_speechApiKey, _speechApiRegion);
        var audioConfig = AudioConfig.FromWavFileInput(recordingUri.AbsoluteUri);
        var recognizer = new SpeechRecognizer(speechConfig, audioConfig);
        var result = await recognizer.RecognizeOnceAsync();
        return result.Text;
    }

    private static async Task<KeyPhraseBatchResult> ExtractKeyPhrasesAsync(string text)
    {
        
        var credentials = new ApiKeyServiceClientCredentials(_analyticApiKey);
        var client = new TextAnalyticsClient(credentials)
        {
            Endpoint = _speechApiEndpoint
        };
        var input = new MultiLanguageInput
        {
            Id = "1",
            Text = text,
            Language = "en"
        };
        var batch = new MultiLanguageBatchInput
        {
            Documents = new List<MultiLanguageInput> { input }
        };
        return await client.KeyPhrasesBatchAsync(batch);
    }

    private static async Task<SentimentBatchResult> ExtractSentimentAsync(string text)
    {
        var credentials = new ApiKeyServiceClientCredentials(_analyticApiKey);
        var client = new TextAnalyticsClient(credentials)
        {
            Endpoint = _analyticEndpoint
        };
        var input = new MultiLanguageInput
        {
            Id = "1",
            Text = text,
            Language = "en"
        };
        var batch = new MultiLanguageBatchInput
        {
            Documents = new List<MultiLanguageInput> { input }
        };
        return await client.SentimentBatchAsync(batch);
    }

    /// <summary>
    /// Sample Summary Generation
    /// </summary>
    /// <param name="keyPhrases"></param>
    /// <param name="sentiment"></param>
    /// <returns>A Short Sample Summary of the meeting</returns>
    private static string GenerateSummary(
        KeyPhraseBatchResult keyPhrases,
        SentimentBatchResult sentiment)
    {
        var functionUrl = Environment.GetEnvironmentVariable("GenerateSummaryFunctionUrl");

        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, functionUrl)
        {
            Content = new StringContent($"{{\"keyPhrases\": {JsonSerializer.Serialize(keyPhrases)}, \"sentiment\": {JsonSerializer.Serialize(sentiment)}}}",
                                        System.Text.Encoding.UTF8,
                                        "application/json"),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        var response = client.SendAsync(request).Result;
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception("Error calling summary generation function.");
        }
        return response.Content.ReadAsStringAsync().Result;
    }

}