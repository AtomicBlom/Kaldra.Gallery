using BinaryVibrance.Kaldra.Core.Graph;
using Kaldra.Gallery.Demos;
using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Name scoping (<see cref="GraphBuilder.Scope"/>): the mechanism that lets one reusable graph fragment be
/// instantiated more than once in a single graph. The point of the feature is that the <em>fragment</em> needs no
/// parameter and no change, so the load-bearing test here is the one that instantiates the real
/// <see cref="BloomGraph"/> twice.
/// </summary>
public class GraphScopeTests
{
	[Test]
	public void A_scope_qualifies_resources_domains_and_passes()
	{
		var graph = RenderGraph.Define(g =>
		{
			using (g.Scope("left"))
			{
				var world = g.Domain("world", 320, 180);
				var lit = world.Color("lit", Format.Rgba16F);
				g.Pass("shade").Writes(lit);
			}
		});

		graph.Domains.Select(d => d.Name).ShouldContain("left.world");
		graph.Resources.Select(r => r.Name).ShouldContain("left.lit");
		graph.Passes.Select(p => p.Name).ShouldContain("left.shade");
	}

	[Test]
	public void Declarations_outside_a_scope_are_unqualified()
	{
		var graph = RenderGraph.Define(g =>
		{
			var outside = g.Screen.Color("outside", Format.Rgba16F);
			using (g.Scope("inner"))
				g.Screen.Color("inside", Format.Rgba16F);
			var after = g.Screen.Color("after", Format.Rgba16F);
			g.Pass("use").Samples(outside, after).Writes(g.Backbuffer);
		});

		var names = graph.Resources.Select(r => r.Name).ToArray();
		names.ShouldContain("outside");
		names.ShouldContain("inner.inside");
		names.ShouldContain("after"); // the scope closed, so the prefix stopped applying
	}

	[Test]
	public void Scopes_nest()
	{
		var graph = RenderGraph.Define(g =>
		{
			using (g.Scope("outer"))
			using (g.Scope("middle"))
			using (g.Scope("inner"))
				g.Screen.Color("target", Format.Rgba16F);
		});

		graph.Resources.Select(r => r.Name).ShouldContain("outer.middle.inner.target");
	}

	[Test]
	public void A_dotted_scope_name_is_the_same_as_nesting()
	{
		var dotted = RenderGraph.Define(g =>
		{
			using (g.Scope("view.left"))
				g.Screen.Color("lit", Format.Rgba16F);
		});
		var nested = RenderGraph.Define(g =>
		{
			using (g.Scope("view"))
			using (g.Scope("left"))
				g.Screen.Color("lit", Format.Rgba16F);
		});

		dotted.Hash().ShouldBe(nested.Hash());
	}

	[Test]
	[Arguments("")]
	[Arguments("   ")]
	[Arguments(".leading")]
	[Arguments("trailing.")]
	[Arguments("double..dot")]
	public void A_scope_name_with_an_empty_segment_is_rejected(string name) =>
		// An empty segment would put a bare '.' into every qualified name and open an unnamed debug-label group.
		Should.Throw<ArgumentException>(() => RenderGraph.Define(g =>
		{
			using (g.Scope(name)) { }
		}));

	[Test]
	public void An_unclosed_scope_fails_the_build() =>
		Should.Throw<InvalidOperationException>(() => RenderGraph.Define(g =>
		{
			var scope = g.Scope("leaked"); // deliberately not disposed
			g.Screen.Color("lit", Format.Rgba16F);
			_ = scope;
		})).Message.ShouldContain("leaked");

	[Test]
	public void The_backbuffer_keeps_its_name_inside_a_scope()
	{
		// The backbuffer is created lazily on first access, so which scope touches it first is an accident of
		// authoring order. Two fragments that both write the screen must reach the same physical image.
		var graph = RenderGraph.Define(g =>
		{
			using (g.Scope("first"))
				g.Pass("blit").Writes(g.Backbuffer);
			using (g.Scope("second"))
				g.Pass("overlay").Blends(g.Backbuffer);
			g.Present(g.Backbuffer);
		});

		graph.Resources.Count(r => r.Kind == ResourceKind.Backbuffer).ShouldBe(1);
		graph.Resources.Single(r => r.Kind == ResourceKind.Backbuffer).Name.ShouldBe("<backbuffer>");
		graph.Domains.Count(d => d.IsBackbuffer).ShouldBe(1);
	}

	[Test]
	public void The_same_name_in_two_scopes_does_not_collide()
	{
		var graph = RenderGraph.Define(g =>
		{
			using (g.Scope("a"))
				g.Screen.Color("lit", Format.Rgba16F);
			using (g.Scope("b"))
				g.Screen.Color("lit", Format.Rgba16F);
		});

		// g.Screen mints the backbuffer, so filter to what the two scopes actually declared.
		graph.Resources.Where(r => !r.IsBackbuffer).Select(r => r.Name).OrderBy(n => n).ShouldBe(["a.lit", "b.lit"]);
	}

	[Test]
	public void The_same_name_twice_in_one_scope_still_collides() =>
		// Scoping widens the namespace; it does not switch the duplicate check off. The check sees qualified names.
		Should.Throw<InvalidOperationException>(() => RenderGraph.Define(g =>
		{
			using (g.Scope("a"))
			{
				g.Screen.Color("lit", Format.Rgba16F);
				g.Screen.Color("lit", Format.Rgba16F);
			}
		})).Message.ShouldContain("a.lit");

	[Test]
	public void The_bloom_fragment_can_be_instantiated_twice_unchanged()
	{
		// The whole point of the feature, against the real shipped fragment: BloomGraph.Bloom declares fixed names
		// (bloom_mip0.., lit_bloomed) and fixed pass names, and takes no prefix parameter. Two calls collided before.
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var leftLit = g.Screen.Color("lit_left", Format.Rgba16F);
			var rightLit = g.Screen.Color("lit_right", Format.Rgba16F);
			g.Pass("scene.left").Writes(leftLit);
			g.Pass("scene.right").Writes(rightLit);

			ColorHandle left, right;
			using (g.Scope("left"))
				left = g.Bloom(leftLit);
			using (g.Scope("right"))
				right = g.Bloom(rightLit);

			g.Pass("combine").Samples(left, right).Writes(screen);
			g.Present(screen);
		});

		var names = graph.Resources.Select(r => r.Name).ToArray();
		names.ShouldContain("left.bloom_mip0");
		names.ShouldContain("right.bloom_mip0");
		names.ShouldContain("left.lit_bloomed");
		names.ShouldContain("right.lit_bloomed");
		graph.Passes.Select(p => p.Name).ShouldContain("left.bloom.prefilter");
		graph.Passes.Select(p => p.Name).ShouldContain("right.bloom.prefilter");

		// And the result still derives: each instance's mip chain is an independent dependency chain.
		Should.NotThrow(() => GraphSchedule.Derive(graph));
	}

	[Test]
	public void Two_instantiations_of_a_fragment_produce_independent_schedules()
	{
		// Names are how the graph tells the two copies apart, so a scoping bug would show up as the second bloom's
		// passes depending on the first's mips. Assert the counts instead of trusting that Derive succeeded.
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var lit = g.Screen.Color("lit", Format.Rgba16F);
			g.Pass("scene").Writes(lit);
			ColorHandle a, b;
			using (g.Scope("a"))
				a = g.Bloom(lit, mips: 3);
			using (g.Scope("b"))
				b = g.Bloom(lit, mips: 3);
			g.Pass("combine").Samples(a, b).Writes(screen);
			g.Present(screen);
		});

		// 3 mips + the composited output, per instance.
		graph.Resources.Count(r => r.Name.StartsWith("a.")).ShouldBe(4);
		graph.Resources.Count(r => r.Name.StartsWith("b.")).ShouldBe(4);
		graph.Passes.Count(p => p.Name.StartsWith("a.")).ShouldBe(graph.Passes.Count(p => p.Name.StartsWith("b.")));
	}
}
