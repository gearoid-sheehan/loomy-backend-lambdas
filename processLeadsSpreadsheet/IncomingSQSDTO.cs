﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace processLeadsSpreadsheet
{
    internal class IncomingSQSDTO
    {
        public string Email { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string GreenscreenTemplateVideoURL { get; set; } = "";
        public string GreenscreenTemplateVideoS3Key { get; set; } = "";
        public string ExcelFileURL { get; set; } = "";
        public string ExcelFileS3Key { get; set; } = "";
    }
}
