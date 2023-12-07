namespace uploadProjectToVimeo
{
    public class SQSDTO
    {
        public string LeadId { get; set; } = "";
        public string Email { get; set; } = "";
        public string ProcessedVideoS3Key { get; set; } = "";
        public string LeadProcessedVideoTitle { get; set; } = "";
        public string FolderUri { get; set; } = "";
        public bool VideoProcessingSuccessful { get; set; } = true;
    }
}
