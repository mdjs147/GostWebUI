using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;

namespace PortForwarder.Web
{
    // 静态文件托管:优先用 exe 同目录下的 wwwroot(开发构建会复制到输出目录,改前端无需改发布物);
    // 磁盘不存在时回退到嵌入资源(单文件发布时前端已嵌入 exe,产物无需携带 wwwroot 目录)。
    public static class StaticFiles
    {
        // EmbeddedResource 默认资源名前缀 = RootNamespace + 目录名(见 csproj 的 wwwroot 嵌入项)
        private const string EmbeddedBaseNamespace = "PortForwarder.wwwroot";

        public static void Configure(WebApplication app)
        {
            IFileProvider fileProvider = CreateFileProvider();

            DefaultFilesOptions defaultFiles = new DefaultFilesOptions();
            defaultFiles.FileProvider = fileProvider;
            app.UseDefaultFiles(defaultFiles);

            StaticFileOptions staticOptions = new StaticFileOptions();
            staticOptions.FileProvider = fileProvider;
            app.UseStaticFiles(staticOptions);
        }

        private static IFileProvider CreateFileProvider()
        {
            string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (File.Exists(Path.Combine(webRoot, "index.html")))
            {
                return new PhysicalFileProvider(webRoot);
            }
            return new EmbeddedFileProvider(typeof(StaticFiles).Assembly, EmbeddedBaseNamespace);
        }
    }
}
