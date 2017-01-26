using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Html;
using Orchard.DisplayManagement.Implementation;

namespace Orchard.DisplayManagement.Descriptors.ShapeTemplateStrategy
{
    public interface IShapeTemplateViewEngine
    {
        IEnumerable<string> TemplateFileExtensions { get; }
        Task<IHtmlContent> RenderAsync(ShapeDescriptor shapeDescriptor, DisplayContext displayContext, HarvestShapeInfo harvestShapeInfo);
    }

    public interface IRazorShapeTemplateViewEngine : IShapeTemplateViewEngine
    {
    }

    public interface IHandlebarsShapeTemplateViewEngine : IShapeTemplateViewEngine
    {
    }
}