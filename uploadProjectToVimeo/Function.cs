using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
using Amazon.DynamoDBv2.DataModel;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using SendGrid.Helpers.Mail;
using SendGrid;
using System.Configuration;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace uploadProjectToVimeo;

public class Function
{
    private readonly string _environmentName = "";
    public IConfiguration Configuration { get; private set; }
    private static readonly HttpClient _httpClient = new();

    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDBContext _context;
    private readonly IAmazonS3 _s3Client;
    private const string VimeoApiBaseUrl = "https://api.vimeo.com";
    private readonly string _s3ProcessedVideosBucketName = "";
    private readonly string _projectTableName = "";
    private readonly string sendGridApiKey = "";
    private readonly string accessToken = "";

    public Function()
    {
        Configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{_environmentName}.json", optional: true)
            .Build();

        _s3Client = new AmazonS3Client();
        _s3ProcessedVideosBucketName = "processed-lead-videos-dev";
        _client = new AmazonDynamoDBClient();
        _context = new DynamoDBContext(_client);
        _projectTableName = _context.GetTargetTable<Project>().TableName;
        sendGridApiKey = Configuration.GetValue<string>("SendGridSettings:ApiKey") ?? "";
        accessToken = Configuration.GetValue<string>("VimeoSettings:ApiKey") ?? "";
    }

    public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
    {
        foreach (var message in evnt.Records)
        {
            await ProcessMessageAsync(message, context);
        }
    }

    public async Task ProcessMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context)
    {
        var sqsDTO = JsonConvert.DeserializeObject<SQSDTO>(message.Body);

        context.Logger.LogLine($"LeadId: {sqsDTO?.LeadId}");

        context.Logger.LogLine($"Uploading video {sqsDTO?.LeadProcessedVideoTitle} from bucket {_s3ProcessedVideosBucketName} with the id {sqsDTO?.ProcessedVideoS3Key}");

        var lead = await GetLead(sqsDTO?.LeadId ?? "");

        if (sqsDTO?.VideoProcessingSuccessful == true)
        {
            var request = new GetObjectRequest
            {
                BucketName = _s3ProcessedVideosBucketName,
                Key = sqsDTO?.ProcessedVideoS3Key
            };

            using (var response = await _s3Client.GetObjectAsync(request))
            using (var stream = response.ResponseStream)
            using (var memoryStream = new MemoryStream())
            {
                await stream.CopyToAsync(memoryStream);
                var videoBytes = memoryStream.ToArray();

                var videoUri = await UploadVideoToVimeo(videoBytes, _s3ProcessedVideosBucketName, sqsDTO?.ProcessedVideoS3Key ?? "", sqsDTO?.LeadProcessedVideoTitle ?? "", sqsDTO?.FolderUri ?? "", context);

                // Process the uploaded videoUri as per your requirements
                context.Logger.LogLine($"Uploaded video URI {videoUri} to Vimeo");

                await UpdateLeadVimeoLink(sqsDTO?.LeadId ?? "", videoUri);
            }
        }
        else
        {
            await UpdateLeadVimeoLink(sqsDTO?.LeadId ?? "", "Failed to Process");
        }

        var project = await UpdateProjectStatusDynamoDB(lead?.ProjectId ?? "");

        if (project?.NumberOfLeadsProcessed == project?.TotalNumberOfLeads)
        {
            context.Logger.LogLine($"Upload all leads Completed");

            var requestLeads = new QueryRequest
            {
                TableName = "Leads",
                IndexName = "ProjectId-index",
                KeyConditionExpression = "ProjectId = :projectId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":projectId", new AttributeValue { S = lead ?.ProjectId ?? "" } } // Make sure project.Id is of the correct data type
                }
            };

            // Execute the query
            var response = await _client.QueryAsync(requestLeads);

            // Extract the items from the response
            var leads = response.Items;

            // Create a memory stream to hold the CSV data
            using (var memoryStream = new MemoryStream())
            using (var streamWriter = new StreamWriter(memoryStream, Encoding.UTF8))
            using (var csv = new CsvWriter(streamWriter, new CsvConfiguration(CultureInfo.InvariantCulture)))
            {
                // Don't need SortKey so left it out
                csv.WriteRecords(leads.Select(item => new CSV
                {
                    Id = item["Id"].S,
                    LeadProcessedVideoTitle = item["LeadProcessedVideoTitle"].S,
                    VimeoLink = item["VimeoLink"].S
                }));

                csv.Flush();

                // Reset the stream position to the beginning
                memoryStream.Seek(0, SeekOrigin.Begin);

                var client = new SendGridClient(sendGridApiKey);

                var msg = new SendGridMessage()
                {
                    From = new EmailAddress("luisl@theacquisitionapex.com", "Luis Lagunas"),
                    Subject = "Loomy Processed Leads",
                    PlainTextContent = "Hello, Please find attached a CSV with the Vimeo links to the processed leads videos"
                };

                msg.AddTo(new EmailAddress(sqsDTO?.Email ?? "", ""));

                // Attach the CSV data as an attachment
                msg.AddAttachment(
                    "processed_leads.csv",
                    Convert.ToBase64String(memoryStream.ToArray()), // Convert the MemoryStream to a base64-encoded string
                    "text/csv"
                );

                var sendgridResponse = client.SendEmailAsync(msg).Result;

                if (sendgridResponse.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    context.Logger.LogLine("Email sent successfully!");
                }
                else
                {
                    context.Logger.LogLine($"Email sending failed with status code: {sendgridResponse.StatusCode}");
                }
            }
        }
    }

    private async Task<string> UploadVideoToVimeo(byte[] videoBytes, string s3BucketName, string s3Key, string fileName, string folderUri, ILambdaContext context)
    {
        context.Logger.LogLine($"Uploading to Folder: " + folderUri);

        var requestUrl = $"{VimeoApiBaseUrl}/me/videos";

        var bodyParams = new
        {
            upload = new
            {
                approach = "pull",
                size = videoBytes.Length,
                link = $"https://{s3BucketName}.s3.{_s3Client.Config.RegionEndpoint.SystemName}.amazonaws.com/{s3Key}"
            },

            name = fileName,
            folder_uri = folderUri,
            privacy = new
            {
                view = "unlisted"
            }
        };

        context.Logger.LogLine($"Video length in bytes:  {bodyParams.upload.size}");

        context.Logger.LogLine($"Pulling from link: {bodyParams.upload.link}");

        var jsonBody = JsonConvert.SerializeObject(bodyParams);

        var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.vimeo.*+json"));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request);

        context.Logger.LogLine($"Response: " + response.ToString());

        if (response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            dynamic videoData = JsonConvert.DeserializeObject(responseBody) ?? "";
            string videoUri = videoData?.link ?? "";

            return videoUri;
        }
        else
        {
            throw new Exception($"Error uploading video: {response.StatusCode}");
        }
    }

    private async Task<Project?> UpdateProjectStatusDynamoDB(string projectId)
    {
        var request = new UpdateItemRequest
        {
            TableName = "Projects",
            Key = new Dictionary<string, AttributeValue>
        {
            { "Id", new AttributeValue { S = projectId } },
            { "SortKey", new AttributeValue { S = projectId } }
        },
            UpdateExpression = "SET NumberOfLeadsProcessed = NumberOfLeadsProcessed + :incr",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            { ":incr", new AttributeValue { N = "1" } }
        },
            ReturnValues = ReturnValue.ALL_NEW
        };

        await _client.UpdateItemAsync(request);

        var updatedProject = await _context.QueryAsync<Project>(projectId).GetRemainingAsync();

        if (updatedProject != null && updatedProject.Any())
        {
            return updatedProject.FirstOrDefault();
        }
        else
        {
            return null;
        }
    }

    private async Task<Lead?> GetLead(string leadId)
    {
        var response = await _context.QueryAsync<Lead>(leadId).GetRemainingAsync();

        if (response != null && response.Any())
        {
            return response.FirstOrDefault();
        }
        else
        {
            return null;
        }
    }

    private async Task UpdateLeadVimeoLink(string leadId, string uploadLink)
    {
        Dictionary<string, AttributeValueUpdate> updates = new Dictionary<string, AttributeValueUpdate>();

        updates["VimeoLink"] = new AttributeValueUpdate()
        {
            Action = AttributeAction.PUT,
            Value = new AttributeValue { S = uploadLink }
        };

        var request = new UpdateItemRequest
        {
            TableName = "Leads",
            Key = new Dictionary<string, AttributeValue>
                {
                    {
                        "Id", new AttributeValue { S = leadId }
                    },
                    {
                        "SortKey", new AttributeValue { S =  leadId }
                    }
                },

            AttributeUpdates = updates
        };

        await _client.UpdateItemAsync(request);
    }
}
