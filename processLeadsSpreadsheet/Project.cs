using Amazon.DynamoDBv2.DataModel;

namespace processLeadsSpreadsheet
{
    [DynamoDBTable("Projects")]
    internal class Project
    {
        public Project()
        {

        }

        [DynamoDBHashKey]
        public string Id { get; set; } = "";

        [DynamoDBProperty]
        public string SortKey { get; set; } = "";

        [DynamoDBProperty]
        public string Email { get; set; } = "";

        [DynamoDBProperty]
        public int NumberOfLeads { get; set; } = 0;

        [DynamoDBProperty]
        public int TotalNumberOfLeads { get; set; } = 0;

        [DynamoDBProperty]
        public string GreenscreenTemplateVideoS3Key { get; set; } = "";

        [DynamoDBProperty]
        public int NumberOfLeadsProcessed { get; set; } = 0;

        //[DynamoDBProperty]
        //public bool IsFinishedProcessingLeads { get; set; } = false;
    }
}
