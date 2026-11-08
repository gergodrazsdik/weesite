---
title: "A weekend with Haskell's type classes"
slug: a-weekend-with-haskell-type-classes
date: 2026-08-24
author: example-author
lang: en
tags: [haskell, notes]
summary: "Functor, Foldable and a small parser, written for fun."
---

Type classes describe what a type can do, separately from what it is.

```haskell
data Tree a = Leaf | Node (Tree a) a (Tree a)

instance Functor Tree where
  fmap _ Leaf = Leaf
  fmap f (Node l x r) = Node (fmap f l) (f x) (fmap f r)

instance Foldable Tree where
  foldr _ z Leaf = z
  foldr f z (Node l x r) = foldr f (f x (foldr f z r)) l

-- Sum of a tree, for free from Foldable
total :: Tree Int -> Int
total = sum
```

## What stuck

- `Foldable` gives `sum`, `length` and `elem` once you write `foldr`.
- Laws matter: `fmap id` must be `id`, and the compiler won't check it for you.
