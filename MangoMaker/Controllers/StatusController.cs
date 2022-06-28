using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MangoMaker.Controllers
{
    [Route("status")]
    public class StatusController : BaseController
    {
        [AllowAnonymous]
        [Route("")]
        [HttpGet]
        public async Task<ContentResult> Status()
        {
            try
            {
                StringBuilder sb = new();

                await using (FileStream fs = System.IO.File.Open($"session-{DateTime.Now.ToString("yyyyMMdd")}.log", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buf = new byte[1024];
                    int c;

                    while ((c = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        sb.Append(Encoding.UTF8.GetString(buf, 0, c));
                    }
                }

                return Content(sb.ToString());
            }
            catch (Exception e)
            {
                return Content(e.ToString());
            }
        }
    }
}
