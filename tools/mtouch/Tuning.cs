using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.XPath;

using Mono.Linker;
using Mono.Linker.Steps;

using Mono.Cecil;
using Mono.Tuner;

using Xamarin.Bundler;
using Xamarin.Linker;
using Xamarin.Linker.Steps;

namespace MonoTouch.Tuner {

	public class LinkerOptions {
		public AssemblyDefinition MainAssembly { get; set; }
		public string OutputDirectory { get; set; }
		public LinkMode LinkMode { get; set; }
		public AssemblyResolver Resolver { get; set; }
		public IEnumerable<string> SkippedAssemblies { get; set; }
		public I18nAssemblies I18nAssemblies { get; set; }
		public bool LinkSymbols { get; set; }
		public bool LinkAway { get; set; }
		public bool Device { get; set; }
		public bool EnsureUIThread { get; set; }
		public IList<string> ExtraDefinitions { get; set; }
		public bool OldRegistrar { get; set; }
		public bool DebugBuild { get; set; }
		public int Arch { get; set; }
		public bool IsDualBuild { get; set; }
		public bool Unified { get; set; }
		public bool DumpDependencies { get; set; }
		internal PInvokeWrapperGenerator MarshalNativeExceptionsState { get; set; }
		internal RuntimeOptions RuntimeOptions { get; set; }

		public MonoTouchLinkContext LinkContext { get; set; }

		public static I18nAssemblies ParseI18nAssemblies (string i18n)
		{
			var assemblies = I18nAssemblies.None;

			foreach (var part in i18n.Split (',')) {
				var assembly = part.Trim ();
				if (string.IsNullOrEmpty (assembly))
					continue;

				try {
					assemblies |= (I18nAssemblies) Enum.Parse (typeof (I18nAssemblies), assembly, true);
				} catch {
					throw new FormatException ("Unknown value for i18n: " + assembly);
				}
			}

			return assemblies;
		}
	}

	class Linker {

		public static void Process (LinkerOptions options, out MonoTouchLinkContext context, out List<string> assemblies)
		{
			var pipeline = CreatePipeline (options);

			pipeline.PrependStep (new MobileResolveMainAssemblyStep (options.MainAssembly));

			context = CreateLinkContext (options, pipeline);
			context.Resolver.AddSearchDirectory (options.OutputDirectory);

			if (options.DumpDependencies) {
				var prepareDependenciesDump = context.Annotations.GetType ().GetMethod ("PrepareDependenciesDump", new Type[1] { typeof (string) });
				if (prepareDependenciesDump != null)
					prepareDependenciesDump.Invoke (context.Annotations, new object[1] { string.Format ("{0}{1}linker-dependencies.xml.gz", options.OutputDirectory, Path.DirectorySeparatorChar) });
			}

			try {
				pipeline.Process (context);
			} catch (FileNotFoundException fnfe) {
				// Cecil throw this if the assembly is not found
				throw new MonoTouchException (2002, true, fnfe, fnfe.Message);
			} catch (AggregateException) {
				throw;
			} catch (MonoTouchException) {
				throw;
			} catch (ResolutionException re) {
				TypeReference tr = (re.Member as TypeReference);
				IMetadataScope scope = tr == null ? re.Member.DeclaringType.Scope : tr.Scope;
				throw new MonoTouchException (2002, true, re, "Failed to resolve \"{0}\" reference from \"{1}\"", re.Member, scope);
			} catch (Exception e) {
				throw new MonoTouchException (2001, true, e, "Could not link assemblies. Reason: {0}", e.Message);
			}

			assemblies = ListAssemblies (context);
		}

		static MonoTouchLinkContext CreateLinkContext (LinkerOptions options, Pipeline pipeline)
		{
			var context = new MonoTouchLinkContext (pipeline, options.Resolver);
			context.CoreAction = options.LinkMode == LinkMode.None ? AssemblyAction.Copy : AssemblyAction.Link;
			context.LinkSymbols = options.LinkSymbols;
			context.OutputDirectory = options.OutputDirectory;
			context.SetParameter ("debug-build", options.DebugBuild.ToString ());

			options.LinkContext = context;

			return context;
		}
		
		static SubStepDispatcher GetSubSteps (LinkerOptions options)
		{
			SubStepDispatcher sub = new SubStepDispatcher ();
			sub.Add (new ApplyPreserveAttribute ());
			sub.Add (new CoreRemoveSecurity ());
			sub.Add (new OptimizeGeneratedCodeSubStep (options));
			// OptimizeGeneratedCodeSubStep needs [GeneratedCode] so it must occurs before RemoveAttributes
			sub.Add (new RemoveAttributes ());
			// http://bugzilla.xamarin.com/show_bug.cgi?id=1408
			if (options.LinkAway)
				sub.Add (new RemoveCode (options));
			sub.Add (new MarkNSObjects ());
			sub.Add (new PreserveSoapHttpClients ());
			// there's only one registrar for unified, i.e. DynamicRegistrar
			if (!options.Unified)
				sub.Add (new RemoveExtraRegistrar (options.OldRegistrar));
			sub.Add (new CoreHttpMessageHandler (options));
			sub.Add (new CoreTlsProviderStep (options));
			return sub;
		}

		static SubStepDispatcher GetPostLinkOptimizations (LinkerOptions options)
		{
			SubStepDispatcher sub = new SubStepDispatcher ();
			sub.Add (new MetadataReducerSubStep ());
			sub.Add (new SealerSubStep ());
			return sub;
		}

		static Pipeline CreatePipeline (LinkerOptions options)
		{
			var pipeline = new Pipeline ();

			pipeline.AppendStep (new LoadReferencesStep ());

			if (options.I18nAssemblies != I18nAssemblies.None)
				pipeline.AppendStep (new LoadI18nAssemblies (options.I18nAssemblies));

			// that must be done early since the XML files can "add" new assemblies [#15878]
			// and some of the assemblies might be (directly or referenced) SDK assemblies
			foreach (string definition in options.ExtraDefinitions)
				pipeline.AppendStep (GetResolveStep (definition));

			if (options.LinkMode != LinkMode.None)
				pipeline.AppendStep (new BlacklistStep ());

			pipeline.AppendStep (new CustomizeIOSActions (options.LinkMode, options.SkippedAssemblies));

			// We need to store the Field attribute in annotations, since it may end up removed.
			pipeline.AppendStep (new ProcessExportedFields ());

			if (options.LinkMode != LinkMode.None) {
				pipeline.AppendStep (new MonoTouchTypeMapStep ());

				pipeline.AppendStep (GetSubSteps (options));

				pipeline.AppendStep (new PreserveCode (options));

				// only remove bundled resources on device builds as MonoDevelop requires the resources later 
				// (to be extracted). That differs from the device builds (where another unmodified copy is used)
				if (options.Device)
					pipeline.AppendStep (new RemoveMonoTouchResources ());

				pipeline.AppendStep (new RemoveResources (options.I18nAssemblies)); // remove collation tables

				pipeline.AppendStep (new MonoTouchMarkStep ());
				pipeline.AppendStep (new MonoTouchSweepStep ());
				pipeline.AppendStep (new CleanStep ());

				if (!options.DebugBuild)
					pipeline.AppendStep (GetPostLinkOptimizations (options));

				pipeline.AppendStep (new RemoveSelectors ());
				pipeline.AppendStep (new FixModuleFlags ());
			}

			pipeline.AppendStep (new ListExportedSymbols (options.MarshalNativeExceptionsState));

			pipeline.AppendStep (new OutputStep ());

			return pipeline;
		}

		static List<string> ListAssemblies (MonoTouchLinkContext context)
		{
			var list = new List<string> ();
			foreach (var assembly in context.GetAssemblies ()) {
				if (context.Annotations.GetAction (assembly) == AssemblyAction.Delete)
					continue;

				list.Add (GetFullyQualifiedName (assembly));
			}

			return list;
		}

		static string GetFullyQualifiedName (AssemblyDefinition assembly)
		{
			return assembly.MainModule.FullyQualifiedName;
		}

		static ResolveFromXmlStep GetResolveStep (string filename)
		{
			filename = Path.GetFullPath (filename);

			if (!File.Exists (filename))
				throw new MonoTouchException (2004, true, "Extra linker definitions file '{0}' could not be located.", filename);

			try {
				using (StreamReader sr = new StreamReader (filename)) {
					return new ResolveFromXmlStep (new XPathDocument (new StringReader (sr.ReadToEnd ())));
				}
			}
			catch (Exception e) {
				throw new MonoTouchException (2005, true, e, "Definitions from '{0}' could not be parsed.", filename);
			}
		}
	}

	public class MonoTouchLinkContext : LinkContext {
		Dictionary<string, MemberReference> required_symbols;
		List<MethodDefinition> marshal_exception_pinvokes;

		public Dictionary<string, MemberReference> RequiredSymbols {
			get {
				if (required_symbols == null)
					required_symbols = new Dictionary<string, MemberReference> ();
				return required_symbols;
			}
		}

		public List<MethodDefinition> MarshalExceptionPInvokes {
			get {
				if (marshal_exception_pinvokes == null)
					marshal_exception_pinvokes = new List<MethodDefinition> ();
				return marshal_exception_pinvokes;
			}
		}

		public MonoTouchLinkContext (Pipeline pipeline, AssemblyResolver resolver)
			: base (pipeline, resolver)
		{
		}
	}

	public class CustomizeIOSActions : CustomizeActions
	{
		LinkMode link_mode;

		public CustomizeIOSActions (LinkMode mode, IEnumerable<string> skipped_assemblies)
			: base (mode == LinkMode.SDKOnly, skipped_assemblies)
		{
			link_mode = mode;
		}

		protected override bool IsLinked (AssemblyDefinition assembly)
		{
			if (link_mode == LinkMode.None)
				return false;
			
			return base.IsLinked (assembly);
		}

		protected override void ProcessAssembly (AssemblyDefinition assembly)
		{
			if (link_mode == LinkMode.None) {
				Annotations.SetAction (assembly, AssemblyAction.Copy);
				return;
			}

			base.ProcessAssembly (assembly);
		}
	}
}
