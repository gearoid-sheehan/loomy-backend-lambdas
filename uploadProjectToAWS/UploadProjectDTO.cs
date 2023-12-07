using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace uploadProjectToAWS
{
    public class UploadProjectDTO
    {
        public string Email { get; set; } = "";
        public string GreenscreenTemplateVideoTitle { get; set; } = "";
        public string ExcelFileTitle { get; set; } = "";
    }
}
