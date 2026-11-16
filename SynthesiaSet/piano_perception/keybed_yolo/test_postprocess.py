"""Regression checks for confidence filtering and key-mask suppression."""

import unittest

import numpy as np

from evaluate import Instance, Mask, box_overlaps
from postprocess import postprocess_instances


def detection(slot, cls, score, values, x=0, y=0):
    height, width = values.shape
    return Instance(cls, np.array([x, y, x + width, y + height]), Mask.create(x, y, values), score, slot)


class PostprocessTests(unittest.TestCase):
    def test_oblique_neighbors_survive_overlapping_boxes(self):
        diagonal = np.eye(12, dtype=bool)
        first = detection(0, 1, .9, diagonal)
        neighbor = detection(1, 1, .8, diagonal, x=3)
        duplicate = detection(2, 1, .7, diagonal)

        self.assertGreater(box_overlaps([first], [neighbor])[0, 0], .5)
        kept = postprocess_instances([duplicate, neighbor, first], (.3, .3, .3), mask_iou=.35)
        self.assertEqual([item.slot for item in kept], [0, 1])

    def test_class_specific_cutoffs_can_recover_low_confidence_keys(self):
        shape = np.ones((4, 4), dtype=bool)
        bed = detection(0, 0, .7, shape)
        white = detection(1, 1, .2, shape)
        black = detection(2, 2, .125, shape)
        exact_cutoff = detection(3, 2, .0625, shape, x=5)
        instances = [exact_cutoff, white, black, bed]

        baseline = postprocess_instances(instances, (.3, .3, .3), mask_iou=.35)
        self.assertEqual([item.slot for item in baseline], [0])
        kept = postprocess_instances(instances, (.3, .1, .0625), mask_iou=.35)
        self.assertEqual([item.slot for item in kept], [0, 1, 2])

    def test_containment_removes_similar_subset_without_removing_much_smaller_mask(self):
        first = detection(0, 1, .9, np.ones((4, 4), dtype=bool))
        subset = detection(1, 1, .8, np.ones((3, 4), dtype=bool), y=1)
        small = detection(2, 1, .7, np.ones((1, 4), dtype=bool))

        kept = postprocess_instances([first, subset, small], (.3, .3, .3), mask_iou=.8, containment=.9)
        self.assertEqual([item.slot for item in kept], [0, 2])

    def test_empty_mask_cannot_suppress_a_real_detection(self):
        empty = detection(0, 1, .9, np.zeros((4, 4), dtype=bool))
        valid = detection(1, 1, .8, np.ones((4, 4), dtype=bool))
        zero_width = detection(2, 1, .7, np.zeros((4, 0), dtype=bool))

        kept = postprocess_instances([empty, valid, zero_width], (.3, .3, .3), mask_iou=.35)
        self.assertEqual([item.slot for item in kept], [1])

    def test_equal_scores_keep_input_order(self):
        shape = np.ones((4, 4), dtype=bool)
        first = detection(0, 1, .8, shape)
        duplicate = detection(1, 1, .8, shape)

        kept = postprocess_instances([duplicate, first], (.3, .3, .3), mask_iou=.35)
        self.assertEqual([item.slot for item in kept], [1])

    def test_bed_margin_filters_remote_candidates_and_preserves_nearby_keys(self):
        bed = detection(0, 0, .8, np.ones((4, 20), dtype=bool))
        outside_edge = detection(1, 1, .9, np.ones((4, 3), dtype=bool), x=22)
        remote = detection(2, 2, .7, np.ones((3, 2), dtype=bool), y=16)

        kept = postprocess_instances([remote, outside_edge, bed], (.3, .3, .3), max_bed_distance=1)
        self.assertEqual([item.slot for item in kept], [1, 0])

    def test_bed_margin_is_inactive_without_a_surviving_bed(self):
        low_score_bed = detection(0, 0, .1, np.ones((4, 20), dtype=bool))
        empty_bed = detection(1, 0, .9, np.zeros((4, 20), dtype=bool))
        remote = detection(2, 2, .7, np.ones((3, 2), dtype=bool), y=16)

        kept = postprocess_instances([low_score_bed, empty_bed, remote], (.3, .3, .3), max_bed_distance=1)
        self.assertEqual([item.slot for item in kept], [2])


if __name__ == "__main__":
    unittest.main()
