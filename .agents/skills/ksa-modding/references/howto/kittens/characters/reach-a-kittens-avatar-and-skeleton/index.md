# Game Feature

* [Reach a kitten's avatar, skeleton, bones and animation catalog](overview.md) - How a mod gets from a KittenEva to its CharacterAvatar, Skeleton, named bones, private clip fields and materials, and where in the frame the kitten is animated and drawn

# How-To

* [Build a live clip catalog from cached FieldInfo reads with a visible unresolved list](reach-a-kittens-avatar-and-skeleton-2.md) - unscience reads the thirteen private clip fields, four blend samplers and four anim processors of KittenRenderable by name, adds the public MMU and overlay clips, and shows any name that no longer resolves in the UI so a game update degrades visibly
* [Know where and when a kitten is animated and drawn, per viewport](reach-a-kittens-avatar-and-skeleton-4.md) - An engine explainer for the kitten draw path - viewports record secondary first and the main one last, KittenEva.UpdateRenderData animates and submits per view, dt is zeroed after the first call in a frame, processors are culled by distance times Priority, and Draw fills the shadow pool
* [Reach the avatar with one reflection hop and resolve bones by name](reach-a-kittens-avatar-and-skeleton-1.md) - KSAGolf reads the private KittenRenderable._characterAvatar field once through a single helper, resolves bone names to pose-span indices with Skeleton.BoneNames.IndexOf and reports unknown names instead of throwing
* [Walk the avatar's struct fields down to the live MaterialIndices arrays](reach-a-kittens-avatar-and-skeleton-3.md) - doh follows a path of field names from KittenEva through KittenRenderable._characterAvatar and the boxed Core, Fur, Attachments, Helmet and Mmu structs to the protected int[] MaterialIndices of each kitten renderable, which stays a reference to the live array
