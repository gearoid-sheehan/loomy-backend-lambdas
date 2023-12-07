using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Text;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace uploadProjectToAWS;

public class Function
{
    private readonly string _environmentName = "";
    public IConfiguration _configuration { get; private set; }
    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonSQS _sqsClient;
    private readonly IAmazonDynamoDB _dynamoDBClient;
    private readonly DynamoDBContext _dynamoDBContext;
    private readonly HttpClient _httpClient;
    private readonly string _projectTableName = "";
    private readonly string _vimeoAccessToken = "";
    private readonly string _s3BucketGreenscreenTemplateVideos = "";
    private readonly string _s3BucketLeadsSpreadsheets = "";
    private readonly string _processLeadsSpreadsheetQueue = "";  

    public Function()
    {
        _environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "";

        _configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{_environmentName}.json", optional: true)
        .Build();

        // Get the AWS region value from appsettings.json
        string awsRegion = _configuration.GetValue<string>("AWS:Region") ?? "";

        // Create an S3 client with specific signature version
        var s3Config = new AmazonS3Config
        {
            SignatureVersion = "4",
            RegionEndpoint = RegionEndpoint.GetBySystemName(awsRegion)
        };

        _sqsClient = new AmazonSQSClient();
        _httpClient = new HttpClient();
        _s3Client = new AmazonS3Client(s3Config);
        _dynamoDBClient = new AmazonDynamoDBClient();
        _dynamoDBContext = new DynamoDBContext(_dynamoDBClient);
        _projectTableName = _dynamoDBContext.GetTargetTable<Project>().TableName;
        _vimeoAccessToken = _configuration.GetValue<string>("Vimeo:AccessToken") ?? "";
        _s3BucketGreenscreenTemplateVideos = _configuration.GetValue<string>("AWS:S3BucketGreenscreenTemplateVideos") ?? "";
        _s3BucketLeadsSpreadsheets = _configuration.GetValue<string>("AWS:S3BucketLeadsSpreadsheets") ?? "";
        _processLeadsSpreadsheetQueue = _configuration.GetValue<string>("AWS:SQSProcessLeadsSpreadsheet") ?? "";
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        UploadProjectDTO? uploadProjectDTO = JsonConvert.DeserializeObject<UploadProjectDTO>(request.Body);

        context.Logger.LogLine("Greenscreen Template Video Title: " + uploadProjectDTO?.GreenscreenTemplateVideoTitle);
        context.Logger.LogLine("Excel File Title: " + uploadProjectDTO?.ExcelFileTitle);

        string greenscreenTemplateVideoS3Key = Guid.NewGuid().ToString() + Path.GetExtension(uploadProjectDTO?.GreenscreenTemplateVideoTitle);
        string excelFileS3Key = Guid.NewGuid().ToString() + Path.GetExtension(uploadProjectDTO?.ExcelFileTitle);

        context.Logger.LogLine("Greenscreen Template Video S3 Key: " + greenscreenTemplateVideoS3Key);
        context.Logger.LogLine("Excel File S3 Key: " + excelFileS3Key);

        var headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "Access-Control-Allow-Origin", "*" }
            };

        try
        {
            DateTime expiryTime = DateTime.Now.AddMinutes(20);

            string greenscreenTemplateVideoURL = GeneratePresignedS3URL(greenscreenTemplateVideoS3Key, expiryTime, true, _s3BucketGreenscreenTemplateVideos, context);
            string excelFileURL = GeneratePresignedS3URL(excelFileS3Key, expiryTime, false, _s3BucketLeadsSpreadsheets, context);

            context.Logger.LogLine("Greenscreen Template Video Presigned URL Generated: " + greenscreenTemplateVideoURL);
            context.Logger.LogLine("Excel File Presigned URL Generated: " + excelFileURL);

            if (string.IsNullOrWhiteSpace(greenscreenTemplateVideoURL) || string.IsNullOrWhiteSpace(excelFileURL))
            {
                return new APIGatewayProxyResponse
                {
                    StatusCode = 500,
                    Body = "An error occurred: Url is Null or Whitespace",
                    Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
                };
            }

            var greenscreenTemplateVideoResult = new GeneratePresignedUrlResult
            {
                PresignedUrl = greenscreenTemplateVideoURL,
                FileName = greenscreenTemplateVideoS3Key
            };

            var excelFileResult = new GeneratePresignedUrlResult
            {
                PresignedUrl = excelFileURL,
                FileName = excelFileS3Key
            };

            List<GeneratePresignedUrlResult> presignedUrlResults = new List<GeneratePresignedUrlResult>
            {
                greenscreenTemplateVideoResult,
                excelFileResult
            };

            var body = JsonConvert.SerializeObject(presignedUrlResults);

            string projectId = Guid.NewGuid().ToString();

            // Send to file to the SQS for uploading to Vimeo
            SQSDTO sendToBatchProcessingDTO = new SQSDTO()
            {
                Email = uploadProjectDTO?.Email ?? "",
                ProjectId = projectId,
                GreenscreenTemplateVideoS3Key = greenscreenTemplateVideoS3Key,
                ExcelFileS3Key = excelFileS3Key,
                ExcelFileURL = excelFileURL
            };

            await WriteToProcessLeadsSpreadsheetQueue(sendToBatchProcessingDTO, context);

            string folderUri = await CreateVimeoFolder(context, projectId);

            Project project = new()
            {
                Id = projectId,
                SortKey = projectId,
                Email = uploadProjectDTO?.Email  ?? "",
                GreenscreenTemplateVideoS3Key = greenscreenTemplateVideoS3Key,
                FolderUri = folderUri,
                IsFinishedProcessingLeads = false
            };

            await SaveProjectToDynamoDB(project);

            return new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = body,
                Headers = headers
            };
        }
        catch (Exception ex)
        {
            return new APIGatewayProxyResponse
            {
                StatusCode = 500,
                Body = "An error occurred: " + ex.Message,
                Headers = headers
            };
        }
    }

    private string GeneratePresignedS3URL(string s3Key, DateTime expiryTime, bool isVideo, string s3BucketName, ILambdaContext context)
    {
        // check extension is valid
        string? mimeType = CalculateMimeType(s3Key, isVideo, context);

        context.Logger.LogLine("Mime Type: " + mimeType);

        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return "";
        }

        GetPreSignedUrlRequest request = new GetPreSignedUrlRequest
        {
            BucketName = s3BucketName,
            Key = s3Key,
            Expires = expiryTime,
            ContentType = mimeType,
            Verb = HttpVerb.PUT,
            Protocol = Protocol.HTTPS
        };
        
        return _s3Client.GetPreSignedURL(request);
    }

    private async Task WriteToProcessLeadsSpreadsheetQueue(SQSDTO sqsDTO, ILambdaContext context)
    {
        var json = JsonConvert.SerializeObject(sqsDTO);

        try
        {
            context.Logger.LogLine("SQS URL: " + _processLeadsSpreadsheetQueue);
            await _sqsClient.SendMessageAsync(_processLeadsSpreadsheetQueue, json);
        }
        catch (Exception ex)
        {
            context.Logger.LogLine("Exception: " + ex.ToString());
        }
    }

    private static string? CalculateMimeType(string extension, bool isVideo, ILambdaContext context)
    {
        List<AcceptedMimetypes> mimetypes = new();

        if (isVideo == true)
        {
            mimetypes = GetAcceptedVideoMimetypes();
        }
        else
        {
            mimetypes = GetAcceptedSpreadsheetMimetypes();
        }

        extension = Path.GetExtension(extension);

        context.Logger.LogLine("Extension: " + extension.ToString());

        var matchedMimeType = mimetypes
            .Where(m => m.Extension.ToLower() == extension.ToLower())
            .FirstOrDefault();

        if (matchedMimeType != null)
        {
            return matchedMimeType.Mimetype;
        }
        else
        {
            return null;
        }
    }

    private static List<AcceptedMimetypes> GetAcceptedVideoMimetypes()
    {
        List<AcceptedMimetypes> mimetypes = new List<AcceptedMimetypes>
        {
            new AcceptedMimetypes
            {
                Extension = ".mp4",
                Mimetype = "video/mp4"
            },

            new AcceptedMimetypes
            {
                Extension = ".webm",
                Mimetype = "video/webm"
            },

            new AcceptedMimetypes
            {
                Extension = ".mkv",
                Mimetype = "video/x-matroska"
            }
        };

        return mimetypes;
    }

    private static List<AcceptedMimetypes> GetAcceptedSpreadsheetMimetypes()
    {
        List<AcceptedMimetypes> mimetypes = new List<AcceptedMimetypes>
        {
            new AcceptedMimetypes
            {
                Extension = ".xlsx",
                Mimetype = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            },

            new AcceptedMimetypes
            {
                Extension = ".xls",
                Mimetype = "application/vnd.ms-excel"
            },

            new AcceptedMimetypes
            {
                Extension = ".csv",
                Mimetype = "text/csv"
            },

            new AcceptedMimetypes
            {
                Extension = ".ods",
                Mimetype = "application / vnd.oasis.opendocument.spreadsheet"
            }
        };

        return mimetypes;
    }

    private async Task<Project> SaveProjectToDynamoDB(Project project)
    {
        await _dynamoDBContext.SaveAsync(project);
        return project;
    }

    private async Task<string> CreateVimeoFolder(ILambdaContext context, string folderName)
    {
        try
        {
            // Construct the URL to create a folder
            var createFolderUrl = "https://api.vimeo.com/me/projects";

            // Create a folder object with the given name
            var folder = new { name = folderName };

            // Serialize the folder object to JSON

            var folderJson = JsonConvert.SerializeObject(folder);

            // Create a POST request with the access token
            var request = new HttpRequestMessage(HttpMethod.Post, createFolderUrl);
            request.Headers.Add("Authorization", $"Bearer {_vimeoAccessToken}");
            request.Content = new StringContent(folderJson, Encoding.UTF8, "application/json");

            // Send the request and get the response
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                // Deserialize the response to get the folder URI
                var responseContent = await response.Content.ReadAsStringAsync();
                var folderResponse = JsonConvert.DeserializeObject<dynamic>(responseContent);
                var folderUri = folderResponse?.uri;

                return folderUri;
            }
            else
            {
                throw new Exception($"Failed to create folder: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"Error: {ex.Message}");
            throw;
        }
    }
}