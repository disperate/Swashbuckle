﻿using System.Linq;
using System.Collections.Generic;
using System.Web.Http.Description;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Swashbuckle.Swagger
{
    public class SwaggerGenerator : ISwaggerProvider
    {
        private readonly IApiExplorer _apiExplorer;
        private readonly IContractResolver _jsonContractResolver;
        private readonly IDictionary<string, Info> _apiVersions;
        private readonly SwaggerGeneratorOptions _options;

        public SwaggerGenerator(
            IApiExplorer apiExplorer,
            IContractResolver jsonContractResolver,
            IDictionary<string, Info> apiVersions,
            SwaggerGeneratorOptions options = null)
        {
            _apiExplorer = apiExplorer;
            _jsonContractResolver = jsonContractResolver;
            _apiVersions = apiVersions;
            _options = options ?? new SwaggerGeneratorOptions();
        }

        public SwaggerDocument GetSwagger(string rootUrl, string apiVersion)
        {
            var schemaRegistry = new SchemaRegistry(_jsonContractResolver, _options.CustomSchemaMappings, _options.SchemaFilters);

            Info info;
            _apiVersions.TryGetValue(apiVersion, out info);
            if (info == null)
                throw new UnknownApiVersion(apiVersion);

            var paths = GetApiDescriptionsFor(apiVersion)
                .OrderBy(_options.GroupingKeySelector, _options.GroupingKeyComparer)
                .GroupBy(apiDesc => apiDesc.RelativePathSansQueryString())
                .ToDictionary(group => "/" + group.Key, group => CreatePathItem(group, schemaRegistry));

            var rootUri = new Uri(rootUrl);

            var swaggerDoc = new SwaggerDocument
            {
                info = info,
                host = rootUri.Host + ":" + rootUri.Port,
                basePath = (rootUri.AbsolutePath != "/") ? rootUri.AbsolutePath : null,
                schemes = (_options.Schemes != null) ? _options.Schemes.ToList() : new[] { rootUri.Scheme }.ToList(),
                paths = paths,
                definitions = schemaRegistry.Definitions,
                securityDefinitions = _options.SecurityDefinitions
            };

            foreach(var filter in _options.DocumentFilters)
            {
                filter.Apply(swaggerDoc, schemaRegistry, _apiExplorer);
            }

            return swaggerDoc;
        }

        private IEnumerable<ApiDescription> GetApiDescriptionsFor(string apiVersion)
        {
            return (_options.VersionSupportResolver == null)
                ? _apiExplorer.ApiDescriptions
                : _apiExplorer.ApiDescriptions.Where(apiDesc => _options.VersionSupportResolver(apiDesc, apiVersion));
        }

        private PathItem CreatePathItem(IEnumerable<ApiDescription> apiDescriptions, SchemaRegistry schemaRegistry)
        {
            var pathItem = new PathItem();

            // Group further by http method
            var perMethodGrouping = apiDescriptions
                .GroupBy(apiDesc => apiDesc.HttpMethod.Method.ToLower());

            foreach (var group in perMethodGrouping)
            {
                var httpMethod = group.Key;

                var apiDescription = (group.Count() == 1)
                    ? group.First()
                    : _options.ConflictingActionsResolver(group);

                switch (httpMethod)
                {
                    case "get":
                        pathItem.get = CreateOperation(apiDescription, schemaRegistry);
                        break;
                    case "put":
                        pathItem.put = CreateOperation(apiDescription, schemaRegistry);
                        break;
                    case "post":
                        pathItem.post = CreateOperation(apiDescription, schemaRegistry);
                        break;
                    case "delete":
                        pathItem.delete = CreateOperation(apiDescription, schemaRegistry);
                        break;
                    case "options":
                        pathItem.options = CreateOperation(apiDescription, schemaRegistry);
                        break;
                    case "head":
                        pathItem.head = CreateOperation(apiDescription, schemaRegistry);
                        break;
                    case "patch":
                        pathItem.patch = CreateOperation(apiDescription, schemaRegistry);
                        break;
                }
            }

            return pathItem;
        }

        private Operation CreateOperation(ApiDescription apiDescription, SchemaRegistry schemaRegistry)
        {
            var parameters = apiDescription.ParameterDescriptions
                .Select(paramDesc =>
                    {
                        var inPath = apiDescription.RelativePathSansQueryString().Contains("{" + paramDesc.Name + "}");
                        return CreateParameter(paramDesc, inPath, schemaRegistry);
                    })
                 .ToList();

            // TODO: Always 200 - is this a little presumptious?
            var responses = new Dictionary<string, Response>{
                { "200", CreateResponse(apiDescription.SuccessResponseType(), schemaRegistry) }
            };

            var operation = new Operation
            { 
                tags = new [] { _options.GroupingKeySelector(apiDescription) },
                operationId = apiDescription.FriendlyId(),
                produces = apiDescription.Produces().ToList(),
                consumes = apiDescription.Consumes().ToList(),
                parameters = parameters.Any() ? parameters : null, // parameters can be null but not empty
                responses = responses,
                deprecated = apiDescription.IsObsolete()
            };

            foreach (var filter in _options.OperationFilters)
            {
                filter.Apply(operation, schemaRegistry, apiDescription);
            }

            return operation;
        }

        private Parameter CreateParameter(ApiParameterDescription paramDesc, bool inPath, SchemaRegistry schemaRegistry)
        {
            var @in = (inPath)
                ? "path"
                : (paramDesc.Source == ApiParameterSource.FromUri) ? "query" : "body";

            var parameter = new Parameter
            {
                name = paramDesc.Name,
                @in = @in
            };

            if (paramDesc.ParameterDescriptor == null)
            {
                parameter.type = "string";
                parameter.required = true;
                return parameter; 
            }

            parameter.required = !paramDesc.ParameterDescriptor.IsOptional;

            var schema = schemaRegistry.FindOrRegister(paramDesc.ParameterDescriptor.ParameterType);
            if (parameter.@in == "body")
                parameter.schema = schema;
            else
                parameter.PopulateFrom(schema);

            return parameter;
        }

        private Response CreateResponse(Type returnType, SchemaRegistry schemaRegistry)
        {
            var schema = (returnType != null)
                ? schemaRegistry.FindOrRegister(returnType)
                : null;

            return new Response
            {
                description = "OK",
                schema = schema
            };
        }
    }
}