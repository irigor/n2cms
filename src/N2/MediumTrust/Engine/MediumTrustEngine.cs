﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Web;
using System.Reflection;
using System.Web.Configuration;
using System.Diagnostics;

using N2.Engine;
using N2.Persistence;
using N2.Web;
using N2.Definitions;
using N2.Integrity;
using N2.Security;
using N2.Edit;
using N2.Persistence.NH;
using N2.Persistence.NH.Finder;
using N2.Serialization;
using N2.MediumTrust.Configuration;
using N2.Details;
using N2.Persistence.Finder;
using N2.MediumTrust.Persistence.NH;
using N2.Edit.Settings;
using N2.Plugin;
using N2.Parts;
using Castle.Core;
using N2.Configuration;
using N2.Globalization;

namespace N2.MediumTrust.Engine
{
	public class MediumTrustEngine : IEngine
	{
		private readonly IPersister persister;
		private readonly IUrlParser urlParser;
		private readonly IUrlRewriter rewriter;
		private readonly IDefinitionManager definitions;
		private readonly IIntegrityManager integrityManager;
		private readonly ISecurityManager securityManager;
		private readonly IEditManager editManager;
		private readonly ISessionProvider sessionProvider;
		private readonly ISecurityEnforcer securityEnforcer;
		private readonly IHost host;
		private readonly RequestLifeCycleHandler lifeCycleHandler;
		private readonly ItemFinder finder;
		private readonly IDictionary<Type, object> resolves = new Dictionary<Type, object>();
		private readonly VersionManager versioner;
		private readonly ConfigurationBuilder nhBuilder;
		private readonly DefaultItemNotifier notifier;
		private readonly ITypeFinder typeFinder;
		private readonly IntegrityEnforcer integrityEnforcer;
		private readonly NHRepository<int, ContentItem> itemRepository;
		private readonly NHRepository<int, LinkDetail> linkRepository;
		private readonly ItemXmlReader xmlReader;
		private readonly ItemXmlWriter xmlWriter;
		private readonly DefinitionBuilder definitionBuilder;
		private readonly NHibernate.IInterceptor interceptor;
		private readonly IWebContext webContext;

		public MediumTrustEngine()
			: this(null)
		{

		}

		public MediumTrustEngine(IWebContext webContext)
		{
			HostSection hostConfiguration = (HostSection)WebConfigurationManager.GetSection("n2/host");
			EngineSection engineConfiguration = (EngineSection)WebConfigurationManager.GetSection("n2/engine");
			DatabaseSection databaseConfiguration = (DatabaseSection)WebConfigurationManager.GetSection("n2/database");
			
			AddComponentInstance("host", typeof(IHost), host = new Host(webContext, hostConfiguration.RootID, hostConfiguration.StartPageID));
			if (webContext == null)
				webContext = new N2.Web.RequestContext();
			Resolves[typeof(IWebContext)] = this.webContext = webContext;
			Resolves[typeof(IItemNotifier)] = notifier = new DefaultItemNotifier();
			Resolves[typeof(ITypeFinder)] = typeFinder = new MediumTrustTypeFinder(webContext, engineConfiguration);
			Resolves[typeof(DefinitionBuilder)] = definitionBuilder = new DefinitionBuilder(typeFinder);
			Resolves[typeof(IDefinitionManager)] = definitions = new DefinitionManager(definitionBuilder, notifier);
			NHibernate.Cfg.Environment.UseReflectionOptimizer = false;
			Resolves[typeof(IConfigurationBuilder)] = nhBuilder = new ConfigurationBuilder(definitions, databaseConfiguration);
			Resolves[typeof(NHibernate.IInterceptor)] = interceptor = new NotifyingInterceptor(notifier);
			Resolves[typeof(ISessionProvider)] = sessionProvider = new SessionProvider(nhBuilder, interceptor, webContext);
			Resolves[typeof(IItemFinder)] = finder = new ItemFinder(sessionProvider, definitions);
			Resolves[typeof(INHRepository<int, ContentItem>)] = itemRepository = new NHRepository<int, ContentItem>(sessionProvider);
			Resolves[typeof(INHRepository<int, LinkDetail>)] = linkRepository = new NHRepository<int, LinkDetail>(sessionProvider);
			Resolves[typeof(IPersister)] = persister = new ContentPersister(itemRepository, linkRepository, finder);

			if (hostConfiguration.MultipleSites)
			{
				ISitesProvider sitesProvider = new DynamicSitesProvider(persister, host.DefaultSite.RootItemID);
				Resolves[typeof(ISitesProvider)] = sitesProvider;
				Resolves[typeof(IUrlParser)] = urlParser = new MultipleSitesParser(persister, webContext, notifier, host, sitesProvider, hostConfiguration);
			}
			else
			{
				Resolves[typeof(IUrlParser)] = urlParser = new UrlParser(persister, webContext, notifier, host);
			}
			Resolves[typeof(ISecurityManager)] = securityManager = new SecurityManager(webContext);
			Resolves[typeof(ISecurityEnforcer)] = securityEnforcer = new SecurityEnforcer(persister, securityManager, urlParser, webContext);
			Resolves[typeof(IVersionManager)] = versioner = new VersionManager(persister, itemRepository);
			NavigationSettings settings = new NavigationSettings(webContext);
			Resolves[typeof(NavigationSettings)] = settings;
			Resolves[typeof(IEditManager)] = editManager = new EditManager(typeFinder, definitions, persister, versioner, securityManager, settings);
			Resolves[typeof(IIntegrityManager)] = integrityManager = new IntegrityManager(definitions, urlParser);
			Resolves[typeof(IIntegrityEnforcer)] = integrityEnforcer = new IntegrityEnforcer(persister, integrityManager);
			Resolves[typeof(IUrlRewriter)] = rewriter = new UrlRewriter(urlParser, webContext);
			Resolves[typeof(IRequestLifeCycleHandler)] = lifeCycleHandler = new RequestLifeCycleHandler(rewriter, securityEnforcer, sessionProvider, webContext);
			Resolves[typeof(ItemXmlReader)] = xmlReader = new ItemXmlReader(definitions);
			Resolves[typeof(Importer)] = new Importer(persister, xmlReader);
			Resolves[typeof(ItemXmlWriter)] = xmlWriter = new ItemXmlWriter(definitions, urlParser);
			Resolves[typeof(Exporter)] = new Exporter(xmlWriter);
			AddComponentInstance("", typeof(ILanguageGateway), new LanguageGateway(persister, finder, editManager, definitions, host));
			AddComponentInstance("initializerInvoker", typeof(IPluginBootstrapper), new PluginBootstrapper(typeFinder));
			AddComponentInstance("navigator", typeof(Navigator), new Navigator(persister, host));

			AttributeExplorer<IServiceEditable> serviceExplorer = new AttributeExplorer<IServiceEditable>();
			AttributeExplorer<IEditableContainer> containerExplorer = new AttributeExplorer<IEditableContainer>();
			SettingsManager settingsManager = new SettingsManager(serviceExplorer, containerExplorer);

			foreach (KeyValuePair<Type, object> pair in resolves)
			{
				settingsManager.Handle(pair.Key.Name, pair.Value.GetType());
			}

			EditableHierarchyBuilder<IServiceEditable> hierarchyBuilder = new EditableHierarchyBuilder<IServiceEditable>();
			Resolves[typeof(ISettingsProvider)] = new SettingsProvider(settingsManager, hierarchyBuilder);

			securityEnforcer.Start();
			integrityEnforcer.Start();

			RegisterParts();
		}

		private void RegisterParts()
		{
			AjaxRequestDispatcher dispatcher = new AjaxRequestDispatcher(securityManager);
			AddComponentInstance("AjaxRequestDispatcher", typeof(AjaxRequestDispatcher), dispatcher);
			CreateUrlProvider cup = new CreateUrlProvider(persister, editManager, definitions, dispatcher);
			cup.Start();
			AddComponentInstance("CreateUrlProvider", typeof(CreateUrlProvider), cup);
			ItemDeleter id = new ItemDeleter(persister, dispatcher);
			id.Start();
			AddComponentInstance("ItemDeleter", typeof(ItemDeleter), id);
			EditUrlProvider eud = new EditUrlProvider(persister, editManager, dispatcher);
			eud.Start();
			AddComponentInstance("EditUrlProvider", typeof(EditUrlProvider), eud);
			ItemMover im = new ItemMover(persister, dispatcher);
			im.Start();
			AddComponentInstance("ItemMover", typeof(ItemMover), im);
			ItemCopyer ic = new ItemCopyer(persister, dispatcher);
			ic.Start();
			AddComponentInstance("ItemCopyer", typeof(ItemCopyer), ic);
		}

		#region Properties
		public IPersister Persister
		{
			get { return persister; }
		}

		public IUrlParser UrlParser
		{
			get { return urlParser; }
		}

		public IUrlRewriter Rewriter
		{
			get { return rewriter; }
		}

		public IDefinitionManager Definitions
		{
			get { return definitions; }
		}

		public IIntegrityManager IntegrityManager
		{
			get { return integrityManager; }
		}

		public ISecurityManager SecurityManager
		{
			get { return securityManager; }
		}

		public IEditManager EditManager
		{
			get { return editManager; }
		}

        public IHost Host
        {
            get { return host; }
        }

		public IDictionary<Type, object> Resolves
		{
			get { return resolves; }
		}
		#endregion

		#region Methods
		public void InitializePlugins()
		{
			Debug.WriteLine("MediumTrustEngine: initializing plugins");

			IPluginBootstrapper invoker = Resolve<IPluginBootstrapper>();
			invoker.InitializePlugins(this, invoker.GetPluginDefinitions());
		}

		public void Attach(HttpApplication application)
		{
			lifeCycleHandler.Init(application);
		}

		public T Resolve<T>() where T : class
		{
			return Resolve(typeof(T)) as T;
		}

		public object Resolve(Type serviceType)
		{
			if (Resolves.ContainsKey(serviceType))
				return Resolves[serviceType];
			else
				throw new N2Exception("Couldn't find any service of the type " + serviceType);
		}


		public object Resolve(string key)
		{
			foreach (KeyValuePair<Type, object> pair in resolves)
			{
				if (pair.Key.Name == key)
					return pair.Value;
			}
			return null;
		}

		public void AddComponent(string key, Type classType)
		{
			AddComponent(key, classType, classType);
		}

		public void AddComponent(string key, Type serviceType, Type classType)
		{
			ConstructorInfo constructor = FindBestCosntructor(classType);
			object[] parameters = CreateConstructorParameters(constructor.GetParameters());
			object componentInstance = constructor.Invoke(parameters);
			
			AddComponentInstance(key, serviceType, componentInstance);
		}

		protected virtual object[] CreateConstructorParameters(ParameterInfo[] parameterInfos)
		{
			object[] parameters = new object[parameterInfos.Length];
			for (int i = 0; i < parameterInfos.Length; i++)
			{
				parameters[i] = Resolve(parameterInfos[i].ParameterType);
			}
			return parameters;
		}

		protected virtual ConstructorInfo FindBestCosntructor(Type classType)
		{
			int maxParameters = -1;
			ConstructorInfo bestContructor = null;
			foreach (ConstructorInfo constructor in classType.GetConstructors())
			{
				int parameters = constructor.GetParameters().Length;
				if (parameters > maxParameters)
				{
					bestContructor = constructor;
					maxParameters = parameters;
				}
			}

			return bestContructor;
		}

		public void AddFacility(string key, object facility)
		{
			Trace.WriteLine("MediumTrustEngine.AddFacility not implemented: " + key);
		}

		public void Release(object instance)
		{
			Trace.WriteLine("MediumTrustEngine.Release not implemented: " + instance);
		}

		[Obsolete("Use AddComponentInstance")]
		public void AddComponent(Type serviceType, object instance)
		{
			AddComponentInstance(serviceType.FullName, serviceType, instance);
		}
		public void AddComponentInstance(string key, Type serviceType, object instance)
		{
			Resolves[serviceType] = instance;
			if (instance is IAutoStart)
			{
				(instance as IAutoStart).Start();
			}
		}

		#endregion
    }
}
