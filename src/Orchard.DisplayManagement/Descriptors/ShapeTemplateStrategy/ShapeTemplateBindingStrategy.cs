using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ViewFeatures.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchard.DisplayManagement.Implementation;
using Orchard.Environment.Extensions;
using Orchard.Environment.Extensions.Features;
using Orchard.Environment.Shell;

namespace Orchard.DisplayManagement.Descriptors.ShapeTemplateStrategy
{
    public class ShapeTemplateBindingStrategy : IShapeTableHarvester
    {
        private static ConcurrentDictionary<string, Func<object, string>> _renderers = new ConcurrentDictionary<string, Func<object, string>>();

        private readonly IEnumerable<IShapeTemplateHarvester> _harvesters;
        private readonly IEnumerable<IShapeTemplateViewEngine> _shapeTemplateViewEngines;
        private readonly IOptions<MvcViewOptions> _viewEngine;
        private readonly IHostingEnvironment _hostingEnvironment;
        private readonly ILogger _logger;
        private readonly IShellFeaturesManager _shellFeaturesManager;

        public ShapeTemplateBindingStrategy(
            IEnumerable<IShapeTemplateHarvester> harvesters,
            IShellFeaturesManager shellFeaturesManager,
            IEnumerable<IShapeTemplateViewEngine> shapeTemplateViewEngines,
            IOptions<MvcViewOptions> options,
            IHostingEnvironment hostingEnvironment,
            ILogger<DefaultShapeTableManager> logger)
        {
            _harvesters = harvesters;
            _shellFeaturesManager = shellFeaturesManager;
            _shapeTemplateViewEngines = shapeTemplateViewEngines;
            _viewEngine = options;
            _hostingEnvironment = hostingEnvironment;
            _logger = logger;
        }

        public bool DisableMonitoring { get; set; }

        private static IEnumerable<IExtensionInfo> Once(IEnumerable<IFeatureInfo> featureDescriptors)
        {
            var once = new ConcurrentDictionary<string, object>();
            return featureDescriptors.Select(x => x.Extension).Where(ed => once.TryAdd(ed.Id, null)).ToList();
        }

        public void Discover(ShapeTableBuilder builder)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Start discovering shapes");
            }

            var harvesterInfos = _harvesters
                .Select(harvester => new { harvester, subPaths = harvester.SubPaths() })
                .ToList();

            var enabledFeatures = _shellFeaturesManager.GetEnabledFeaturesAsync().GetAwaiter().GetResult()
                .Where(Feature => !builder.ExcludedFeatureIds.Contains(Feature.Id)).ToList();

            var activeExtensions = Once(enabledFeatures);

            var matcher = new Matcher();
            foreach (var extension in _shapeTemplateViewEngines.SelectMany(x => x.TemplateFileExtensions))
            {
                matcher.AddInclude(string.Format("*.{0}", extension));
            }

            var hits = activeExtensions.Select(extensionDescriptor =>
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Start discovering candidate views filenames");
                }

                var pathContexts = harvesterInfos.SelectMany(harvesterInfo => harvesterInfo.subPaths.Select(subPath =>
                {
                    var subPathFileInfo = _hostingEnvironment
                        .GetExtensionFileInfo(extensionDescriptor, subPath);

                    var directoryInfo = new DirectoryInfo(subPathFileInfo.PhysicalPath);

                    var virtualPath = Path.Combine(extensionDescriptor.SubPath, subPath);

                    if (!directoryInfo.Exists)
                    {
                        return new
                        {
                            harvesterInfo.harvester,
                            subPath,
                            virtualPath,
                            files = new IFileInfo[0]
                        };
                    }

                    var matches = matcher
                        .Execute(new DirectoryInfoWrapper(directoryInfo))
                        .Files;

                    var files = matches
                        .Select(match => _hostingEnvironment
                            .GetExtensionFileInfo(extensionDescriptor, Path.Combine(subPath, match.Path))).ToArray();

                    return new { harvesterInfo.harvester, subPath, virtualPath, files };
                })).ToList();

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Done discovering candidate views filenames");
                }
                var fileContexts = pathContexts.SelectMany(pathContext => _shapeTemplateViewEngines.SelectMany(ve =>
                {
                    return pathContext.files.Select(
                        file => new
                        {
                            fileName = Path.GetFileNameWithoutExtension(file.Name),
                            fileVirtualPath = "~/" + Path.Combine(pathContext.virtualPath, file.Name),
                            physicalPath = file.PhysicalPath,
                            pathContext
                        });
                }));

                var shapeContexts = fileContexts.SelectMany(fileContext =>
                {
                    var harvestShapeInfo = new HarvestShapeInfo
                    {
                        SubPath = fileContext.pathContext.subPath,
                        FileName = fileContext.fileName,
                        TemplateVirtualPath = fileContext.fileVirtualPath,
                        PhysicalPath = fileContext.physicalPath
                    };
                    var harvestShapeHits = fileContext.pathContext.harvester.HarvestShape(harvestShapeInfo);
                    return harvestShapeHits.Select(harvestShapeHit => new { harvestShapeInfo, harvestShapeHit, fileContext });
                });

                return shapeContexts.Select(shapeContext => new { extensionDescriptor, shapeContext }).ToList();
            }).SelectMany(hits2 => hits2);


            foreach (var iter in hits)
            {
                // templates are always associated with the namesake feature of module or theme
                var hit = iter;
                foreach (var feature in hit.extensionDescriptor.Features)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Binding {0} as shape [{1}] for feature {2}",
                            hit.shapeContext.harvestShapeInfo.TemplateVirtualPath,
                            iter.shapeContext.harvestShapeHit.ShapeType,
                            feature.Id);
                    }

                    var fileExtension = Path.GetExtension(hit.shapeContext.harvestShapeInfo.TemplateVirtualPath).TrimStart('.');
                    var viewEngine = _shapeTemplateViewEngines.FirstOrDefault(e => e.TemplateFileExtensions.Contains(fileExtension));

                    builder.Describe(iter.shapeContext.harvestShapeHit.ShapeType)
                        .From(feature)
                        .BoundAs(
                            hit.shapeContext.harvestShapeInfo.TemplateVirtualPath,
                            shapeDescriptor => displayContext => viewEngine.RenderAsync(shapeDescriptor, displayContext, hit.shapeContext.harvestShapeInfo));
                }
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Done discovering shapes");
            }
        }
    }
}